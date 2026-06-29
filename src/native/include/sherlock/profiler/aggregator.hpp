#pragma once

#include <atomic>
#include <cstdint>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include "profilercommon.h"

namespace Sherlock {

class Logger;

/// Aggregates allocations by full call stack (the allocating method plus its
/// callers), entirely in-process, and tracks how many sampled allocations survive
/// their first GC (a cheap proxy for "escapes gen-0").
///
/// Caching is the whole point: the hot path is lock-free — each thread folds
/// allocations into its own shard, keyed by a 64-bit hash of the captured stack —
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
    // owning shard's map — stable, since unordered_map never invalidates element
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
    void endGc();                                              // judge & clear pending
    void countPendingAsSurvived();                             // shutdown: still-live == survived

    /// Merges every thread's shard, resolves frame names (cached), and writes a
    /// folded-stack file sorted by allocated bytes. Must not run concurrently with record().
    void dump(const std::string& path);

    /// Resolves a FunctionID to "Type.Method" (cached). Public so the trace
    /// collector can reuse it as a symbolizer.
    const std::string& resolveMethodName(FunctionID method);

private:
    static constexpr int kMaxShards = 1024;

    Shard& localShard();
    bool survived(ObjectID addr) const;

    ICorProfilerInfo10* info_;
    Logger* logger_;

    // Lock-free shard registry: a thread claims a slot once via fetch_add. Iterated
    // by the GC thread without locking (the world is stopped, so no races), with a
    // null check in case a just-incremented slot isn't populated yet.
    std::atomic<int> shardCount_{0};
    Shard* shards_[kMaxShards] = {};

    // Survivor spans [start, end) by old address, gathered during one GC. GC thread only.
    std::vector<std::pair<ObjectID, ObjectID>> survivorRanges_;

    std::unordered_map<FunctionID, std::string> nameCache_; // dump-time only
};

} // namespace Sherlock
