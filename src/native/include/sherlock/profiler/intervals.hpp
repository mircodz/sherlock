#pragma once

#include <algorithm>
#include <cstdint>
#include <span>
#include <utility>

// Correlation tests liveness at pre-GC addresses before applying compaction moves.
namespace Sherlock::intervals {

// MovedReferences2 relocates [oldStart, oldStart+length) to newStart.
struct MoveRange {
    std::uint64_t oldStart;
    std::uint64_t newStart;
    std::uint64_t length;
};

// Half-open address span [start, end).
using AddrRange = std::pair<std::uint64_t, std::uint64_t>;

// ranges must be sorted by start and non-overlapping.
[[nodiscard]] inline bool inSortedRanges(std::uint64_t addr, std::span<const AddrRange> ranges) {
    auto it = std::upper_bound(ranges.begin(), ranges.end(), addr,
                               [](std::uint64_t a, const AddrRange& r) { return a < r.first; });
    if (it == ranges.begin()) {
        return false;
    }
    --it;
    return addr < it->second;
}

// moves must be sorted by oldStart and non-overlapping. Uncovered addresses stay unchanged.
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

// Single-GC cursor over sorted, non-overlapping ranges. Queries must use non-decreasing
// old addresses; remapped addresses may be out of order across GC heaps.
class ForwardCursor {
public:
    ForwardCursor(std::span<const AddrRange> survivors, std::span<const MoveRange> moves)
        : survivors_(survivors), moves_(moves) {}

    ForwardCursor(std::span<const AddrRange> survivors, std::span<const MoveRange> moves,
                  std::span<const AddrRange> condemned)
        : survivors_(survivors), moves_(moves), condemned_(condemned) {}

    // Uncondemned objects are unexamined, not dead. Empty spans (including the two-arg
    // constructor) treat the whole heap as condemned.
    [[nodiscard]] bool condemned(std::uint64_t addr) {
        if (condemned_.empty()) {
            return true;
        }
        while (c_ < condemned_.size() && condemned_[c_].second <= addr) {
            ++c_;
        }
        return c_ < condemned_.size() && addr >= condemned_[c_].first;
    }

    [[nodiscard]] bool survived(std::uint64_t addr) {
        while (s_ < survivors_.size() && survivors_[s_].second <= addr) {
            ++s_;
        }
        return s_ < survivors_.size() && addr >= survivors_[s_].first;
    }

    // Uncovered addresses stay unchanged.
    [[nodiscard]] std::uint64_t remap(std::uint64_t addr) {
        while (m_ < moves_.size() && moves_[m_].oldStart + moves_[m_].length <= addr) {
            ++m_;
        }
        if (m_ < moves_.size() && addr >= moves_[m_].oldStart) {
            return moves_[m_].newStart + (addr - moves_[m_].oldStart);
        }
        return addr;
    }

private:
    std::span<const AddrRange> survivors_;
    std::span<const MoveRange> moves_;
    std::span<const AddrRange> condemned_;
    std::size_t s_ = 0;
    std::size_t m_ = 0;
    std::size_t c_ = 0;
};

} // namespace Sherlock::intervals
