#pragma once

#include <atomic>
#include <cstdint>
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

/// Aggregates allocations by full call stack (the allocating method plus its
/// callers), entirely in-process, and tracks how many sampled allocations survive
/// their first GC (a cheap proxy for "escapes gen-0").
///
/// Caching is the whole point: the hot path is lock-free - each thread folds
/// allocations into its own shard, keyed by a 64-bit hash of the captured stack -
/// and name resolution (the expensive metadata lookups) is deferred to dump time
/// and memoized, so a given FunctionID is symbolized at most once.
class Aggregator {
public:
    struct Stats {
        std::uint64_t count = 0;
        std::uint64_t bytes = 0;
    };

    // A unique allocation stack and what it has allocated. `frames` is stored
    // leaf -> root (the order DoStackSnapshot yields).
    struct Site {
        std::vector<FunctionID> frames;
        Stats alloc;      // everything sampled at this stack
        Stats survived;   // the subset that survived its first GC
    };

    // A sampled object awaiting its first-GC verdict. `site` points into the
    // owning shard's map - stable, since unordered_map never invalidates element
    // pointers on insert/rehash.
    struct Pending {
        ObjectID addr;
        std::uint64_t bytes;
        Site* site;
    };

    // Sites keyed by stack hash; collisions across distinct stacks are vanishingly
    // unlikely with a 64-bit FNV-1a and would only merge two stacks' counts.
    struct Shard {
        std::unordered_map<std::uint64_t, Site> sites;
        std::vector<Pending> pending;   // sampled objects not yet judged by a GC
    };

    Aggregator(ICorProfilerInfo10* info, Logger* logger);
    ~Aggregator();

    /// Hot path. `frames` is the captured stack (leaf -> root); `addr` is the
    /// object's address. Lock-free: touches only the calling thread's shard.
    void record(const std::vector<FunctionID>& frames, std::uint64_t bytes, ObjectID addr);

    // --- GC integration. All called on the GC thread with the world stopped. ---
    void beginGc();                                            // reset survivor ranges
    void noteSurvivorRange(ObjectID start, std::uint64_t length); // an old-address survivor span
    void noteMove(ObjectID oldStart, ObjectID newStart, std::uint64_t length); // compaction relocation
    void endGc();                                              // judge & clear pending
    void countPendingAsSurvived();                             // shutdown: still-live == survived

    // --- Correlation (opt-in via SHERLOCK_CORRELATE). Tracks live objects across GC
    // moves so a snapshot can be joined to allocation stacks by current address. ---
    void enableCorrelation() { correlate_ = true; }
    void emitCorrelation(const std::string& path);            // live address -> allocation stack

    /// Merges every thread's shard, resolves frame names (cached), and writes a
    /// folded-stack file sorted by allocated bytes. Must not run concurrently with record().
    void dump(const std::string& path);

    /// Resolves a FunctionID to "Type.Method" (cached). Public so the trace
    /// collector can reuse it as a symbolizer.
    const std::string& resolveMethodName(FunctionID method);

    /// Resolves a ClassID to "Ns.Type" (cached). Used by allocation/exception triggers.
    const std::string& resolveTypeName(ClassID classId);

private:
    static constexpr int kMaxShards = 1024;

    // A tracked live object: a monotonic id plus the allocation site it came from.
    struct Live {
        std::uint64_t id;
        Site* site;
    };

    Shard& localShard();
    bool survived(ObjectID addr) const;    // is addr in this GC's survivor spans?
    ObjectID remap(ObjectID addr) const;   // follow addr through this GC's moves

    /// Serializes a built provenance writer to a .slab file. Returns false on I/O error.
    bool writeSlab(const std::string& path, const storage::ProvenanceWriter& pw);

    /// Merges every thread's shard into one map keyed by stack hash. Best-effort on a live
    /// process (races concurrent record()); exact at shutdown when allocations have stopped.
    std::unordered_map<std::uint64_t, Site> mergeShards();

    /// Interns a site's stack (resolving frames root->leaf) into `pw` and returns its stackId.
    std::uint32_t internSiteStack(storage::ProvenanceWriter& pw, const Site& site);

    /// Writes one AllocationRecord per merged site into `pw`.
    void writeProfile(storage::ProvenanceWriter& pw, const std::unordered_map<std::uint64_t, Site>& sites);

    ICorProfilerInfo10* info_;
    Logger* logger_;

    // Lock-free shard registry: a thread claims a slot once via fetch_add. Iterated
    // by the GC thread without locking (the world is stopped, so no races), with a
    // null check in case a just-incremented slot isn't populated yet.
    std::atomic<int> shardCount_{0};
    Shard* shards_[kMaxShards] = {};

    // Survivor spans [start, end) by old address, gathered during one GC. GC thread only.
    std::vector<intervals::AddrRange> survivorRanges_;

    // Correlation state (opt-in via SHERLOCK_CORRELATE). GC thread + shutdown only.
    // live_ maps a live object's current address to its identity+site; keyed by address
    // for the O(1) point ops in the per-GC rebuild (remaps use the sorted moves_ vector).
    bool correlate_ = false;
    std::atomic<std::uint64_t> nextObjectId_{1};
    std::vector<intervals::MoveRange> moves_;      // this GC's relocations, sorted by oldStart
    std::unordered_map<ObjectID, Live> live_;      // current live tracked objects, keyed by address

    std::unordered_map<FunctionID, std::string> nameCache_;      // dump-time only
    std::unordered_map<ClassID, std::string> typeNameCache_;     // for triggers
};

} // namespace Sherlock
