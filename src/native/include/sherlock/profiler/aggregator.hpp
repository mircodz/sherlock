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
#include "sherlock/profiler/method_registry.hpp"

namespace Sherlock {

class Logger;

namespace storage {
class ProvenanceWriter;
}

// Aggregates sampled allocations by stack and type, tracking survival through the first GC.
// Each thread owns a shard; shard locks let control-thread snapshots copy a coherent view.
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

    // Keyed by stack and type: one call site can allocate multiple types. Frames are root -> leaf.
    struct Site {
        std::vector<FrameId> frames;
        ClassID classId = 0;  // the allocated type; resolved to a name at dump time
        Stats alloc;      // everything sampled at this stack+type
        Stats survived;   // the subset that survived its first GC
    };

    // Awaiting its first-GC verdict. site stays valid across shard-map inserts and rehashes.
    struct Pending {
        ObjectID addr;
        std::uint64_t bytes;
        Site* site;
    };

    // A stack/type hash collision merges counts.
    struct Shard {
        std::mutex mutex;
        std::unordered_map<std::uint64_t, Site> sites;
        std::vector<Pending> pending;   // sampled objects not yet judged by a GC
    };

    Aggregator(ICorProfilerInfo10* info, Logger* logger, MethodRegistry& methods);
    ~Aggregator();

    // frames borrows the caller's root -> leaf shadow stack. Updates only this thread's shard;
    // classId is resolved at dump time.
    void record(std::span<const FrameId> frames, std::uint64_t bytes, ObjectID addr, ClassID classId);

    // GC callbacks run with the world stopped; per-heap note calls may be concurrent.
    void beginGc();                                            // reset survivor + condemned ranges
    void noteCondemnedRange(ObjectID start, std::uint64_t length); // a collected generation's span
    void noteLargeObjectRange(ObjectID start, std::uint64_t length); // an LOH/POH generation's span
    void noteSurvivorRange(ObjectID start, std::uint64_t length); // an old-address survivor span
    void noteMove(ObjectID oldStart, ObjectID newStart, std::uint64_t length); // compaction relocation
    void endGc();                                              // judge & clear pending
    void countPendingAsSurvived();                             // shutdown: still-live == survived

    // Opt-in correlation follows GC moves for a snapshot join by current address.
    void enableCorrelation() { correlate_ = true; }
    [[nodiscard]] bool emitCorrelation(const std::string& path) noexcept;

    // Copy shards, resolve names and atomically publish a profile.
    [[nodiscard]] bool dump(const std::string& path) noexcept;

    // Cached ClassID names for allocation/exception triggers.
    const std::string& resolveTypeName(ClassID classId);
    // Owned copy for diagnostics.
    std::vector<LiveObjectInfo> inspectLiveObjects() const;

private:
    static constexpr int kMaxShards = 1024;

    // IDs follow objects across moves. The live set stays address-sorted by merging
    // monotone remap runs; cross-heap moves need not preserve global order.
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

    // Publish a .slab file; false on I/O failure.
    bool writeSlab(const std::string& path, storage::ProvenanceWriter& pw);

    std::vector<Shard*> registeredShards() const;
    std::unordered_map<std::uint64_t, Site> mergeShards(
        std::span<Shard* const> shards);
    void captureState(
        std::unordered_map<std::uint64_t, Site>& sites,
        std::vector<LiveEntry>* live);

    std::uint32_t internSiteStack(storage::ProvenanceWriter& pw, const Site& site);

    void writeProfile(storage::ProvenanceWriter& pw, const std::unordered_map<std::uint64_t, Site>& sites);

    ICorProfilerInfo10* info_;
    Logger* logger_;
    MethodRegistry& methods_;

    // Tags TLS so a new instance at a reused address cannot inherit a freed shard.
    std::uint64_t instanceId_;

    // Publish claimed shard slots atomically for concurrent control-thread snapshots.
    std::atomic<int> shardCount_{0};
    std::array<std::atomic<Shard*>, kMaxShards> shards_{};

    // Survivor spans [start, end) by old address for this GC.
    std::vector<intervals::AddrRange> survivorRanges_;

    // Server GC reports ranges concurrently on heap threads. Serialize vector writes;
    // beginGc/endGc are serialized callbacks and do not take this lock.
    std::mutex noteMutex_;

    // Pre-GC condemned spans. Objects outside them are unexamined, not dead:
    // the CLR reports survivors only for collected generations.
    std::vector<intervals::AddrRange> condemnedRanges_;

    // Admit pending LOH/POH objects when uncondemned: ephemeral GCs do not report them.
    // Do not extend this exception to young SOH objects past the reported gen-0 frontier.
    std::vector<intervals::AddrRange> largeObjectRanges_;

    // GC/shutdown mutate correlation state under correlationMutex_. Each GC splices only
    // the affected address window; scratch-buffer capacity is retained across GCs.
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
    std::mutex typeNameMutex_;
    std::atomic<std::uint64_t> writeSequence_{1};
    std::unordered_map<ClassID, std::string> typeNameCache_;
};

} // namespace Sherlock
