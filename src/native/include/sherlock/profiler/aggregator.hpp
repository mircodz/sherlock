#pragma once

#include <array>
#include <atomic>
#include <cstdint>
#include <mutex>
#include <span>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include "profilercommon.h"
#include "sherlock/profiler/intervals.hpp"

namespace Sherlock {

class Logger;

namespace storage {
class ProvenanceWriter;
}

/// Aggregates allocations by full call stack (the allocating method plus its callers), in-process,
/// and tracks how many sampled allocations survive their first GC (a cheap proxy for "escapes gen-0").
///
/// Each thread folds allocations into its own shard, keyed by a 64-bit hash of the captured stack.
/// The shard lock is uncontended during normal execution and lets a control-thread snapshot copy a
/// coherent view without racing the target's allocation callbacks.
class Aggregator {
public:
    struct Stats {
        std::uint64_t count = 0;
        std::uint64_t bytes = 0;
    };

    struct LiveObjectInfo {
        std::uint64_t address;
        std::uint64_t id;
    };

    // A unique (allocation stack, allocated type) pair and what it has allocated. The same call site
    // can allocate more than one type, so sites are keyed by both. `frames` is stored root -> leaf.
    struct Site {
        std::vector<FunctionID> frames;
        ClassID classId = 0;  // the allocated type; resolved to a name at dump time
        Stats alloc;      // everything sampled at this stack+type
        Stats survived;   // the subset that survived its first GC
    };

    // A sampled object awaiting its first-GC verdict. `site` points into the owning shard's map,
    // stable since unordered_map never invalidates element pointers on insert/rehash.
    struct Pending {
        ObjectID addr;
        std::uint64_t bytes;
        Site* site;
    };

    // Sites keyed by stack hash; a 64-bit FNV-1a collision across distinct stacks would only merge
    // their counts.
    struct Shard {
        std::mutex mutex;
        std::unordered_map<std::uint64_t, Site> sites;
        std::vector<Pending> pending;   // sampled objects not yet judged by a GC
    };

    Aggregator(ICorProfilerInfo10* info, Logger* logger);
    ~Aggregator();

    /// Hot path. `frames` is the captured stack (root -> leaf), a view over the caller's shadow
    /// stack storage (no allocation); `addr` is the object's address; `classId` is its type (stored,
    /// resolved to a name only at dump time). Touches only the calling thread's shard.
    void record(std::span<const FunctionID> frames, std::uint64_t bytes, ObjectID addr, ClassID classId);

    // --- GC integration. All called on the GC thread with the world stopped. ---
    void beginGc();                                            // reset survivor + condemned ranges
    void noteCondemnedRange(ObjectID start, std::uint64_t length); // a collected generation's span
    void noteLargeObjectRange(ObjectID start, std::uint64_t length); // an LOH/POH generation's span
    void noteSurvivorRange(ObjectID start, std::uint64_t length); // an old-address survivor span
    void noteMove(ObjectID oldStart, ObjectID newStart, std::uint64_t length); // compaction relocation
    void endGc();                                              // judge & clear pending
    void countPendingAsSurvived();                             // shutdown: still-live == survived

    // --- Correlation (opt-in via SHERLOCK_CORRELATE). Tracks live objects across GC
    // moves so a snapshot can be joined to allocation stacks by current address. ---
    void enableCorrelation() { correlate_ = true; }
    [[nodiscard]] bool emitCorrelation(const std::string& path) noexcept;

    /// Copies every thread's shard, resolves frame names, and atomically publishes a profile.
    [[nodiscard]] bool dump(const std::string& path) noexcept;

    /// Resolves a FunctionID to "Type.Method" (cached). Public so the trace
    /// collector can reuse it as a symbolizer.
    const std::string& resolveMethodName(FunctionID method);

    /// Resolves a ClassID to "Ns.Type" (cached). Used by allocation/exception triggers.
    const std::string& resolveTypeName(ClassID classId);
    /// Returns a stable copy of the currently tracked correlated objects for diagnostics.
    std::vector<LiveObjectInfo> inspectLiveObjects() const;

private:
    static constexpr int kMaxShards = 1024;

    // A tracked live object: its current address, a monotonic id, and its allocation site. The live
    // set is a vector sorted by address (not a hash map): GC compaction is order-preserving, so
    // remapping every survivor is a monotonic transform that keeps the set sorted, making the per-GC
    // update an allocation-free, hash-free linear sort-merge against the sorted survivor/move ranges,
    // and giving emitCorrelation a sorted address column for free.
    struct LiveEntry {
        ObjectID addr;
        std::uint64_t id;
        Site* site;
    };

    Shard& localShard();
    std::string resolveTypeNameUncached(ClassID classId); // array/nested-aware, ClrMD-compatible spelling
    bool survived(ObjectID addr) const;    // is addr in this GC's survivor spans?
    bool condemned(ObjectID addr) const;   // is addr in a generation this GC collected?
    bool inLargeObjectHeap(ObjectID addr) const; // is addr on the LOH/POH (gen 3/4)?
    ObjectID remap(ObjectID addr) const;   // follow addr through this GC's moves

    /// Serializes a built provenance writer to a .slab file. Returns false on I/O error.
    bool writeSlab(const std::string& path, storage::ProvenanceWriter& pw);

    std::vector<Shard*> registeredShards() const;
    std::unordered_map<std::uint64_t, Site> mergeShards(
        std::span<Shard* const> shards);
    void captureState(
        std::unordered_map<std::uint64_t, Site>& sites,
        std::vector<LiveEntry>* live);

    /// Interns a site's stack (resolving frames root->leaf) into `pw` and returns its stackId.
    std::uint32_t internSiteStack(storage::ProvenanceWriter& pw, const Site& site);

    /// Writes one AllocationRecord per merged site into `pw`.
    void writeProfile(storage::ProvenanceWriter& pw, const std::unordered_map<std::uint64_t, Site>& sites);

    ICorProfilerInfo10* info_;
    Logger* logger_;

    // Unique per-instance id, tags the per-thread shard cache so a new Aggregator sharing a thread
    // (or a stack address) with a destroyed one never reuses its freed shard.
    std::uint64_t instanceId_;

    // A thread claims one registry slot. Atomic pointers make publication safe for the control
    // thread even when a new allocating thread appears while a snapshot is starting.
    std::atomic<int> shardCount_{0};
    std::array<std::atomic<Shard*>, kMaxShards> shards_{};

    // Survivor spans [start, end) by old address, gathered during one GC. GC thread only.
    std::vector<intervals::AddrRange> survivorRanges_;

    // Guards the note*Range vectors below. Under Server GC the runtime delivers SurvivingReferences2 /
    // MovedReferences2 CONCURRENTLY on multiple GC heap threads, so the push into survivorRanges_/moves_
    // must be serialized or two threads can reallocate the same vector at once (double free). beginGc/
    // endGc run single-threaded (GarbageCollectionStarted/Finished are serialized) so they skip this lock.
    std::mutex noteMutex_;

    // Address spans of the generation(s) condemned by this GC (from GetGenerationBounds at GC start).
    // Only objects INSIDE these spans are in scope for the survivor test: the GC reports survivors
    // solely for condemned generations, so a tracked object OUTSIDE (a higher, un-collected gen) is
    // still alive by definition and must be carried over untouched, not dropped as a false death.
    std::vector<intervals::AddrRange> condemnedRanges_;

    // Address spans of the LOH (gen 3) + POH (gen 4), from GetGenerationBounds at GC start. Large
    // objects are never reported to SurvivingReferences2 during an ephemeral GC (the LOH isn't
    // collected then), so a freshly-allocated large object would never pass the survivor admission
    // test and would be dropped from `pending`. We admit a pending object on the LOH/POH but NOT in
    // this GC's condemned set: it's alive by definition (the GC didn't examine it), and it's re-
    // evaluated once a full GC condemns the LOH. Restricting to LOH/POH (rather than a generic "not
    // condemned") avoids falsely admitting a young SOH object past the gen-0 frontier reported at GC start.
    std::vector<intervals::AddrRange> largeObjectRanges_;

    // Correlation state (opt-in via SHERLOCK_CORRELATE). GC thread + shutdown only. live_ is the
    // current live tracked objects, sorted by address. Each GC updates only the contiguous address
    // WINDOW it could have changed, splicing the merged window back in place so the (large, growing)
    // untouched prefix is never copied. windowScratch_ holds the merged window between the sweep and
    // the splice; its capacity is retained across GCs so a steady-state update allocates nothing.
    bool correlate_ = false;
    std::atomic<std::uint64_t> nextObjectId_{1};
    std::vector<intervals::MoveRange> moves_;      // this GC's relocations, sorted by oldStart
    std::vector<LiveEntry> live_;                  // current live tracked objects, sorted by address
    std::vector<LiveEntry> windowScratch_;         // reusable merged-window buffer (capacity retained)
    std::vector<LiveEntry> mergeOut_;              // reusable k-way merge output (capacity retained)
    std::vector<std::size_t> runStarts_;           // start offsets of the monotone runs in windowScratch_
    std::vector<LiveEntry> newSurvivors_;          // this GC's freshly-surviving sampled objects

    mutable std::mutex correlationMutex_;
    std::mutex snapshotMutex_;
    std::mutex nameCacheMutex_;
    std::atomic<std::uint64_t> writeSequence_{1};
    std::unordered_map<FunctionID, std::string> nameCache_;
    std::unordered_map<ClassID, std::string> typeNameCache_;
};

} // namespace Sherlock
