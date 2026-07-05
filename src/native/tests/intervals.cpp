// Tests for the correctness-critical correlation math: following a live object's address
// across a GC (compaction moves + in-place survivors). This is the part that's easy to
// get subtly wrong and that the ABA / address-reuse hazard hinges on.

#include "sherlock/profiler/intervals.hpp"

#include <gtest/gtest.h>

#include <vector>

using namespace Sherlock::intervals;

// --- remap: pre-GC address -> post-GC address ------------------------------------------

TEST(Remap, EmptyMovesIsIdentity) {
    std::vector<MoveRange> moves;
    EXPECT_EQ(remap(0x1000, moves), 0x1000u);
}

TEST(Remap, AddressInsideAMoveIsRelocatedPreservingOffset) {
    // [0x1000,0x1100) -> 0x8000
    std::vector<MoveRange> moves = {{0x1000, 0x8000, 0x100}};
    EXPECT_EQ(remap(0x1000, moves), 0x8000u);        // start
    EXPECT_EQ(remap(0x1040, moves), 0x8040u);        // interior keeps its offset
    EXPECT_EQ(remap(0x10FF, moves), 0x80FFu);        // last byte
}

TEST(Remap, AddressOutsideAnyMoveIsUnchanged) {
    std::vector<MoveRange> moves = {{0x1000, 0x8000, 0x100}};
    EXPECT_EQ(remap(0x0FFF, moves), 0x0FFFu);        // just below
    EXPECT_EQ(remap(0x1100, moves), 0x1100u);        // just past the end (half-open)
    EXPECT_EQ(remap(0x2000, moves), 0x2000u);        // in a gap between moves
}

TEST(Remap, PicksTheRightMoveAmongMany) {
    // Sorted by oldStart, non-overlapping.
    std::vector<MoveRange> moves = {
        {0x1000, 0x9000, 0x100},
        {0x2000, 0x8000, 0x080},
        {0x3000, 0x7000, 0x200},
    };
    EXPECT_EQ(remap(0x1010, moves), 0x9010u);
    EXPECT_EQ(remap(0x2010, moves), 0x8010u);
    EXPECT_EQ(remap(0x3100, moves), 0x7100u);
    EXPECT_EQ(remap(0x2080, moves), 0x2080u);        // one past the 2nd move's end -> gap
}

// --- inSortedRanges: liveness membership (survivor spans) -------------------------------

TEST(InSortedRanges, EmptyIsAlwaysFalse) {
    std::vector<AddrRange> ranges;
    EXPECT_FALSE(inSortedRanges(0x1000, ranges));
}

TEST(InSortedRanges, HalfOpenBoundaries) {
    std::vector<AddrRange> ranges = {{0x1000, 0x1100}};
    EXPECT_FALSE(inSortedRanges(0x0FFF, ranges));    // below
    EXPECT_TRUE(inSortedRanges(0x1000, ranges));     // start is inclusive
    EXPECT_TRUE(inSortedRanges(0x10FF, ranges));     // last byte
    EXPECT_FALSE(inSortedRanges(0x1100, ranges));    // end is exclusive
}

TEST(InSortedRanges, GapsBetweenRangesAreNotLive) {
    std::vector<AddrRange> ranges = {{0x1000, 0x1100}, {0x2000, 0x2100}};
    EXPECT_TRUE(inSortedRanges(0x2050, ranges));
    EXPECT_FALSE(inSortedRanges(0x1500, ranges));    // dead object in the gap
    EXPECT_FALSE(inSortedRanges(0x3000, ranges));    // above everything
}

TEST(InSortedRanges, DeadSourceAddressIsNotResurrectedByReuse) {
    // Object A died at 0x1000; object B survived from 0x5000 and was moved to 0x1000.
    std::vector<AddrRange> survivorOldSpans = {{0x5000, 0x5100}}; // B's *source*
    EXPECT_FALSE(inSortedRanges(0x1000, survivorOldSpans));       // A (old addr) -> dead
    EXPECT_TRUE(inSortedRanges(0x5000, survivorOldSpans));        // B (old addr) -> alive

    std::vector<MoveRange> moves = {{0x5000, 0x1000, 0x100}};     // B: 0x5000 -> 0x1000
    EXPECT_EQ(remap(0x5000, moves), 0x1000u);                     // B lands at reused slot
}
