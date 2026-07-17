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

/// A forward-only cursor over the sorted survivor spans and move ranges of a single GC. When the
/// live set is itself walked in ascending address order, membership and remap become monotonic:
/// each successive query only advances the cursors, never rewinds — turning the per-object
/// O(log R) binary searches into one amortized O(L + R) linear pass. Correctness rests on GC
/// compaction being order-preserving (a survivor's remapped address is non-decreasing in its old
/// address); the caller asserts the emitted run stays sorted.
class ForwardCursor {
public:
    ForwardCursor(std::span<const AddrRange> survivors, std::span<const MoveRange> moves)
        : survivors_(survivors), moves_(moves) {}

    ForwardCursor(std::span<const AddrRange> survivors, std::span<const MoveRange> moves,
                  std::span<const AddrRange> condemned)
        : survivors_(survivors), moves_(moves), condemned_(condemned) {}

    /// True if `addr` lies in a condemned generation's span — i.e. the GC actually looked at it, so
    /// its absence from the survivor spans means it died. An object OUTSIDE every condemned span was
    /// not part of this collection and is alive by definition. Empty condemned span (e.g. a full GC
    /// that supplied none, or the legacy two-arg ctor) means "treat the whole heap as condemned",
    /// preserving the old behavior. `addr` must be non-decreasing across calls.
    [[nodiscard]] bool condemned(std::uint64_t addr) {
        if (condemned_.empty()) {
            return true;
        }
        while (c_ < condemned_.size() && condemned_[c_].second <= addr) {
            ++c_;
        }
        return c_ < condemned_.size() && addr >= condemned_[c_].first;
    }

    /// True if `addr` lies in a survivor span. `addr` must be non-decreasing across calls.
    [[nodiscard]] bool survived(std::uint64_t addr) {
        while (s_ < survivors_.size() && survivors_[s_].second <= addr) {
            ++s_;
        }
        return s_ < survivors_.size() && addr >= survivors_[s_].first;
    }

    /// Follows `addr` through the moves (identity if uncovered). `addr` must be non-decreasing.
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
