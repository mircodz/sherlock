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

// --- ForwardCursor: the monotonic linear-scan equivalent used by the per-GC live-set update ----

TEST(ForwardCursor, MatchesBinarySearchOverAscendingQueries) {
    // A mixed GC: some survivor spans, some of them compacted (moves), some in-place. The cursor is
    // queried in strictly ascending address order (as the sorted live set is swept), and must agree
    // with the binary-search reference for both membership and remap at every point.
    std::vector<AddrRange> survivors = {{0x1000, 0x1100}, {0x2000, 0x2200}, {0x4000, 0x4100}};
    std::vector<MoveRange> moves = {{0x1000, 0x9000, 0x100}, {0x4000, 0x7000, 0x100}}; // 0x2000-span in place

    ForwardCursor cursor(survivors, moves);
    for (std::uint64_t addr = 0x0F00; addr <= 0x4200; addr += 0x40) {
        bool live = cursor.survived(addr);
        EXPECT_EQ(live, inSortedRanges(addr, survivors)) << "membership at " << std::hex << addr;
        if (live) {
            EXPECT_EQ(cursor.remap(addr), remap(addr, moves)) << "remap at " << std::hex << addr;
        }
    }
}

TEST(ForwardCursor, RemapPreservesAscendingOrder) {
    // The load-bearing invariant: sweeping a sorted live set and remapping each survivor yields a
    // still-sorted run (order-preserving compaction). Even with moves that relocate blocks to very
    // different addresses, the *relative* order of surviving addresses is preserved.
    std::vector<AddrRange> survivors = {{0x1000, 0x1100}, {0x3000, 0x3100}, {0x5000, 0x5100}};
    std::vector<MoveRange> moves = {{0x1000, 0x8000, 0x100}, {0x3000, 0x8100, 0x100}, {0x5000, 0x8200, 0x100}};

    ForwardCursor cursor(survivors, moves);
    std::uint64_t prev = 0;
    for (std::uint64_t addr : {0x1000u, 0x1080u, 0x3000u, 0x5000u, 0x50FFu}) {
        ASSERT_TRUE(cursor.survived(addr));
        std::uint64_t mapped = cursor.remap(addr);
        EXPECT_GE(mapped, prev) << "remapped run must be non-decreasing";
        prev = mapped;
    }
}

TEST(ForwardCursor, DeadAddressesBetweenSurvivorsAreDropped) {
    std::vector<AddrRange> survivors = {{0x1000, 0x1100}, {0x2000, 0x2100}};
    std::vector<MoveRange> moves;
    ForwardCursor cursor(survivors, moves);
    EXPECT_TRUE(cursor.survived(0x1000));   // live
    EXPECT_FALSE(cursor.survived(0x1500));  // dead in the gap (cursor advances, does not rewind)
    EXPECT_TRUE(cursor.survived(0x2000));   // live again, past the gap
}

// --- condemned-generation gate: the generational-eviction fix ---------------------------------

TEST(ForwardCursor, EmptyCondemnedTreatsWholeHeapAsCondemned) {
    // Backward-compat: with no condemned spans, condemned() is always true, so the caller falls back
    // to the pure survivor test — the pre-fix behavior (correct for a full GC).
    std::vector<AddrRange> survivors, condemned;
    std::vector<MoveRange> moves;
    ForwardCursor cursor(survivors, moves, condemned);
    EXPECT_TRUE(cursor.condemned(0x1000));
    EXPECT_TRUE(cursor.condemned(0xdeadbeef));
}

TEST(ForwardCursor, UncondemnedObjectIsAliveEvenWithoutSurvivorRecord) {
    // The bug this fixes: a gen-0 GC condemns only [0x8000,0x9000). A promoted gen-2 object at 0x1000
    // is NOT in the condemned span and is NOT reported as a survivor (the GC never looked at it). The
    // caller keeps it precisely because condemned()==false, instead of dropping it as a false death.
    std::vector<AddrRange> condemned = {{0x8000, 0x9000}};   // only gen-0 collected
    std::vector<AddrRange> survivors = {{0x8000, 0x8080}};   // one gen-0 survivor
    std::vector<MoveRange> moves;
    ForwardCursor cursor(survivors, moves, condemned);

    // Old promoted object, below the condemned span: not condemned → the caller carries it over.
    EXPECT_FALSE(cursor.condemned(0x1000));
    // Gen-0 survivor: condemned AND survived → kept.
    EXPECT_TRUE(cursor.condemned(0x8000));
    EXPECT_TRUE(cursor.survived(0x8000));
    // Gen-0 object that died: condemned but NOT a survivor → dropped.
    EXPECT_TRUE(cursor.condemned(0x8500));
    EXPECT_FALSE(cursor.survived(0x8500));
}

TEST(ForwardCursor, CondemnedAdvancesMonotonicallyLikeSurvived) {
    // condemned() shares the ascending-address contract; verify it matches a binary-search reference.
    std::vector<AddrRange> condemned = {{0x2000, 0x2200}, {0x5000, 0x5100}};
    std::vector<AddrRange> survivors;
    std::vector<MoveRange> moves;
    ForwardCursor cursor(survivors, moves, condemned);
    for (std::uint64_t addr = 0x1000; addr <= 0x6000; addr += 0x80) {
        EXPECT_EQ(cursor.condemned(addr), inSortedRanges(addr, condemned))
            << "condemned membership at " << std::hex << addr;
    }
}

// --- non-monotone remap: the Server-GC / cross-segment hazard the endGc re-sort guards -----------

TEST(ForwardCursor, RemapCanReorderSurvivorsUnderInterleavedMoves) {
    // Under Server GC (multiple heaps) or cross-segment promotion, two survivor blocks can be
    // relocated so their post-GC order is the REVERSE of their pre-GC order. This test documents that
    // remap is NOT unconditionally order-preserving — the reason endGc must re-sort live_ rather than
    // assume the swept run stays sorted. Block A [0x1000,0x1100) -> 0x9000; block B [0x2000,0x2100) ->
    // 0x8000. Pre-GC A < B; post-GC remap(A)=0x9000 > remap(B)=0x8000.
    std::vector<AddrRange> survivors = {{0x1000, 0x1100}, {0x2000, 0x2100}};
    std::vector<MoveRange> moves = {{0x1000, 0x9000, 0x100}, {0x2000, 0x8000, 0x100}};
    ForwardCursor cursor(survivors, moves);

    ASSERT_TRUE(cursor.survived(0x1000));
    std::uint64_t a = cursor.remap(0x1000);
    ASSERT_TRUE(cursor.survived(0x2000));
    std::uint64_t b = cursor.remap(0x2000);
    EXPECT_EQ(a, 0x9000u);
    EXPECT_EQ(b, 0x8000u);
    EXPECT_GT(a, b) << "remapped order is reversed — endGc must re-sort, not assume monotonicity";
}

TEST(ForwardCursor, LargeObjectMembershipMatchesReference) {
    // inLargeObjectHeap uses the same inSortedRanges primitive over the LOH/POH spans; a pending large
    // object is admitted when it's in this set but NOT condemned. Verify the membership math directly.
    std::vector<AddrRange> loh = {{0x40000000, 0x40200000}}; // one 2 MB LOH span
    EXPECT_TRUE(inSortedRanges(0x40000000, loh));            // start inclusive
    EXPECT_TRUE(inSortedRanges(0x401fffff, loh));            // last byte
    EXPECT_FALSE(inSortedRanges(0x40200000, loh));           // end exclusive
    EXPECT_FALSE(inSortedRanges(0x3fffffff, loh));           // below
    EXPECT_FALSE(inSortedRanges(0x1000, loh));               // a young SOH address is not on the LOH
}
