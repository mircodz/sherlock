#pragma once

#include <algorithm>
#include <cstdint>
#include <span>
#include <utility>

// Pure address-interval math for correlation: following a live object's address across a
// GC. Extracted from the aggregator so the correctness-critical part (surviving the ABA
// / compaction hazard) is unit-tested in isolation, with no CLR dependency.
namespace Sherlock::intervals {

/// A compaction relocation reported by MovedReferences2: the block [oldStart, oldStart+length)
/// was moved to begin at newStart.
struct MoveRange {
    std::uint64_t oldStart;
    std::uint64_t newStart;
    std::uint64_t length;
};

/// A half-open address span [start, end).
using AddrRange = std::pair<std::uint64_t, std::uint64_t>;

/// True if `addr` lies in any span. `ranges` must be sorted by start and non-overlapping.
[[nodiscard]] inline bool inSortedRanges(std::uint64_t addr, std::span<const AddrRange> ranges) {
    auto it = std::upper_bound(ranges.begin(), ranges.end(), addr,
                               [](std::uint64_t a, const AddrRange& r) { return a < r.first; });
    if (it == ranges.begin()) {
        return false;
    }
    --it;
    return addr < it->second;
}

/// Maps a pre-GC address to its post-GC address by applying the relocations. `moves` must
/// be sorted by oldStart and non-overlapping. Addresses not covered by any move (in-place
/// survivors) are returned unchanged - so this is identity when `moves` is empty.
[[nodiscard]] inline std::uint64_t remap(std::uint64_t addr, std::span<const MoveRange> moves) {
    auto it = std::upper_bound(moves.begin(), moves.end(), addr,
                               [](std::uint64_t a, const MoveRange& m) { return a < m.oldStart; });
    if (it == moves.begin()) {
        return addr;
    }
    --it;
    if (addr < it->oldStart + it->length) {
        return it->newStart + (addr - it->oldStart);
    }
    return addr;
}

} // namespace Sherlock::intervals
