#pragma once

#include <cstdint>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "profilercommon.h"

namespace Sherlock {

class Logger;

/// Aggregates allocations by full call stack (the allocating method plus its
/// callers), entirely in-process.
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
        Stats stats;
    };

    // Sites keyed by stack hash; collisions across distinct stacks are vanishingly
    // unlikely with a 64-bit FNV-1a and would only merge two stacks' counts.
    struct Shard {
        std::unordered_map<std::uint64_t, Site> sites;
    };

    Aggregator(ICorProfilerInfo10* info, Logger* logger);
    ~Aggregator();

    /// Hot path. `frames` is the captured stack (leaf -> root); empty means no
    /// managed frame. Lock-free: touches only the calling thread's shard.
    void record(const std::vector<FunctionID>& frames, std::uint64_t bytes);

    /// Merges every thread's shard, resolves frame names (cached), and writes a
    /// folded-stack file sorted by bytes descending. Must not run concurrently
    /// with record().
    void dump(const std::string& path);

private:
    Shard& localShard();
    const std::string& resolveMethodName(FunctionID method);

    ICorProfilerInfo10* info_;
    Logger* logger_;

    std::mutex shardsMutex_;          // guards shard registration only
    std::vector<Shard*> shards_;      // owned; one per thread that allocated

    std::unordered_map<FunctionID, std::string> nameCache_; // dump-time only
};

} // namespace Sherlock
