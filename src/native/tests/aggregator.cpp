// Full-lifecycle tests for the correlation live-set tracking in Aggregator: driving record() +
// the GC callbacks (beginGc / noteCondemnedRange / noteSurvivorRange / noteMove / endGc) and asserting
// the resulting live set. Aggregator takes ICorProfilerInfo10* only for name resolution (deferred to
// dump time), so record()/endGc()/the live-set math run fine with a null info pointer.
//
// These cover the cases that were historically buggy: generational eviction (gen-2 objects dropped by
// a gen-0 GC), LOH admission (large objects never survivor-reported by an ephemeral GC), Server-GC
// interleaved remap (the k-way merge), and the windowed update's prefix/suffix splicing.

#include "sherlock/profiler/aggregator.hpp"

#include <gtest/gtest.h>

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <span>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "sherlock/storage/container.hpp"
#include "sherlock/storage/profile.hpp"

using namespace Sherlock;

namespace {

// A single dummy stack frame + class id — the correlation live-set logic doesn't depend on their
// values (only the address bookkeeping matters), and name resolution is null-safe.
constexpr FunctionID kFrame = 0x1000;
constexpr ClassID kClass = 0x2000;

// Record one object at `addr` (bytes default 24). Each distinct call site would key a Site; here one
// site is fine since we assert on addresses/ids, not per-site stats.
void alloc(Aggregator& a, std::uint64_t addr, std::uint64_t bytes = 24) {
    FunctionID frames[1] = {kFrame};
    a.record(std::span<const FunctionID>(frames, 1), bytes, static_cast<ObjectID>(addr), kClass);
}

std::vector<std::uint64_t> liveAddrs(const Aggregator& a) {
    std::vector<std::uint64_t> v;
    for (const Aggregator::LiveObjectInfo& object : a.inspectLiveObjects()) {
        v.push_back(object.address);
    }
    return v;
}

// A correlation-enabled aggregator. Aggregator holds atomics (non-copyable/non-movable), so tests
// construct it in place; inheriting exposes all its methods directly (a.beginGc(), a.record(), ...).
struct Agg : Aggregator {
    Agg() : Aggregator(nullptr, nullptr) { enableCorrelation(); }
};

std::filesystem::path tempSlab(std::string_view name) {
    static std::atomic<std::uint64_t> sequence{1};
    return std::filesystem::temp_directory_path() /
           ("sherlock-" + std::string(name) + "-" +
            std::to_string(sequence.fetch_add(1, std::memory_order_relaxed)) + ".slab");
}

std::string readAll(const std::filesystem::path& path) {
    std::ifstream input(path, std::ios::binary);
    return {std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>()};
}

} // namespace

// --- basic survival / death ------------------------------------------------------------------------

TEST(AggregatorLifecycle, SurvivorIsTrackedDeadIsDropped) {
    Agg a;
    alloc(a, 0x1000);
    alloc(a, 0x2000);
    // A gen-0 GC condemning [0x1000, 0x3000). 0x1000 survives in place; 0x2000 dies.
    a.beginGc();
    a.noteCondemnedRange(0x1000, 0x2000);   // [0x1000,0x3000)
    a.noteSurvivorRange(0x1000, 0x8);       // only 0x1000 survives
    a.endGc();
    EXPECT_EQ(liveAddrs(a), (std::vector<std::uint64_t>{0x1000}));
}

TEST(AggregatorLifecycle, MovedSurvivorIsRemapped) {
    Agg a;
    alloc(a, 0x5000);
    a.beginGc();
    a.noteCondemnedRange(0x5000, 0x1000);
    a.noteMove(0x5000, 0x9000, 0x8);        // 0x5000 -> 0x9000 (compacted; noteMove also marks survivor)
    a.endGc();
    EXPECT_EQ(liveAddrs(a), (std::vector<std::uint64_t>{0x9000}));
}

// --- generational: a gen-2 object must NOT be evicted by a gen-0 GC -------------------------------

TEST(AggregatorLifecycle, PromotedObjectSurvivesEphemeralGC) {
    Agg a;
    alloc(a, 0x1000);                        // will be "promoted" (low address = old gen)
    // First GC: full-ish, promotes 0x1000 (condemn a wide range, it survives in place).
    a.beginGc();
    a.noteCondemnedRange(0x1000, 0x8);
    a.noteSurvivorRange(0x1000, 0x8);
    a.endGc();
    ASSERT_EQ(liveAddrs(a), (std::vector<std::uint64_t>{0x1000}));

    // Now a gen-0 GC condemning ONLY a high-address ephemeral window [0x8000,0x9000). 0x1000 is NOT
    // condemned and NOT survivor-reported — it must be carried over, not dropped (the generational bug).
    alloc(a, 0x8000);
    a.beginGc();
    a.noteCondemnedRange(0x8000, 0x1000);
    a.noteSurvivorRange(0x8000, 0x8);        // the gen-0 object survives
    a.endGc();
    EXPECT_EQ(liveAddrs(a), (std::vector<std::uint64_t>{0x1000, 0x8000}));
}

// --- LOH: a large object is never survivor-reported by an ephemeral GC, but is alive ---------------

TEST(AggregatorLifecycle, LargeObjectAdmittedWhenNotCondemned) {
    Agg a;
    // A large object on the LOH at 0x40000000, allocated then a gen-0 GC runs that does NOT collect
    // the LOH. It's on the LOH range but not condemned → must be admitted as alive.
    alloc(a, 0x40000000, 2 * 1024 * 1024);
    a.beginGc();
    a.noteCondemnedRange(0x8000, 0x1000);        // gen-0 ephemeral segment only
    a.noteLargeObjectRange(0x40000000, 0x400000); // LOH span [0x40000000, 0x40400000)
    // no survivor range covers the LOH object (ephemeral GC doesn't report it)
    a.endGc();
    EXPECT_EQ(liveAddrs(a), (std::vector<std::uint64_t>{0x40000000}));
}

TEST(AggregatorLifecycle, LargeObjectDroppedWhenCondemnedAndDead) {
    Agg a;
    alloc(a, 0x40000000, 2 * 1024 * 1024);
    // admit it via an ephemeral GC first
    a.beginGc();
    a.noteCondemnedRange(0x8000, 0x1000);
    a.noteLargeObjectRange(0x40000000, 0x400000);
    a.endGc();
    ASSERT_EQ(liveAddrs(a), (std::vector<std::uint64_t>{0x40000000}));

    // Now a full GC condemns the LOH and the object is NOT a survivor → it must be dropped.
    a.beginGc();
    a.noteCondemnedRange(0x40000000, 0x400000);   // LOH condemned this time
    a.noteLargeObjectRange(0x40000000, 0x400000);
    // no survivor range → dead
    a.endGc();
    EXPECT_TRUE(liveAddrs(a).empty());
}

// --- pending admission across a first GC (newSurvivors_) ------------------------------------------

TEST(AggregatorLifecycle, PendingObjectsAdmittedOnFirstSurvival) {
    Agg a;
    alloc(a, 0x2000);
    alloc(a, 0x1000);   // out of address order on purpose
    alloc(a, 0x3000);
    a.beginGc();
    a.noteCondemnedRange(0x1000, 0x3000);   // [0x1000,0x4000) covers all three
    a.noteSurvivorRange(0x1000, 0x8);
    a.noteSurvivorRange(0x3000, 0x8);       // 0x1000 and 0x3000 survive, 0x2000 dies
    a.endGc();
    EXPECT_EQ(liveAddrs(a), (std::vector<std::uint64_t>{0x1000, 0x3000}));
}

// --- Server GC: interleaved moves produce K>1 runs; the k-way merge must re-sort correctly ---------

TEST(AggregatorLifecycle, ServerGCInterleavedMovesStaySorted) {
    Agg a;
    // Two "heaps": low block A around 0x1000, high block B around 0x2000. Both are live and promoted.
    alloc(a, 0x1000);
    alloc(a, 0x1008);
    alloc(a, 0x2000);
    alloc(a, 0x2008);
    a.beginGc();
    a.noteCondemnedRange(0x1000, 0x8);
    a.noteCondemnedRange(0x1008, 0x8);
    a.noteCondemnedRange(0x2000, 0x8);
    a.noteCondemnedRange(0x2008, 0x8);
    // Heap A moves UP to 0x9000; heap B moves DOWN to 0x8000 — so remapped order reverses relative to
    // source order: sweeping source-sorted live_ yields runs [0x9000,0x9008] then [0x8000,0x8008],
    // which the k-way merge must reorder to a globally sorted result.
    a.noteMove(0x1000, 0x9000, 0x8);
    a.noteMove(0x1008, 0x9008, 0x8);
    a.noteMove(0x2000, 0x8000, 0x8);
    a.noteMove(0x2008, 0x8008, 0x8);
    a.endGc();
    std::vector<std::uint64_t> live = liveAddrs(a);
    EXPECT_TRUE(std::is_sorted(live.begin(), live.end())) << "live set must stay globally sorted";
    EXPECT_EQ(live, (std::vector<std::uint64_t>{0x8000, 0x8008, 0x9000, 0x9008}));
}

// --- windowed update: the untouched prefix/suffix are preserved unchanged --------------------------

TEST(AggregatorLifecycle, WindowedUpdatePreservesPrefixAndSuffix) {
    Agg a;
    // Build a spread-out live set across three address regions via a full-ish GC that admits all.
    for (std::uint64_t addr : {0x1000ull, 0x2000ull, 0x8000ull, 0x9000ull, 0x40000000ull}) {
        alloc(a, addr);
    }
    a.beginGc();
    a.noteCondemnedRange(0x1000, 0x8);
    a.noteCondemnedRange(0x2000, 0x8);
    a.noteCondemnedRange(0x8000, 0x8);
    a.noteCondemnedRange(0x9000, 0x8);
    a.noteCondemnedRange(0x40000000, 0x8);
    for (std::uint64_t addr : {0x1000ull, 0x2000ull, 0x8000ull, 0x9000ull, 0x40000000ull}) {
        a.noteSurvivorRange(addr, 0x8);
    }
    a.endGc();
    ASSERT_EQ(liveAddrs(a),
              (std::vector<std::uint64_t>{0x1000, 0x2000, 0x8000, 0x9000, 0x40000000}));

    // A gen-0 GC touching ONLY the middle window [0x8000,0x9008): 0x8000 dies, 0x9000 survives. The
    // prefix (0x1000,0x2000) and suffix (0x40000000) must be untouched.
    alloc(a, 0x8800);   // a new gen-0 object in the window that survives
    a.beginGc();
    a.noteCondemnedRange(0x8000, 0x1008);   // [0x8000, 0x9008)
    a.noteSurvivorRange(0x9000, 0x8);
    a.noteSurvivorRange(0x8800, 0x8);
    a.endGc();
    EXPECT_EQ(liveAddrs(a),
              (std::vector<std::uint64_t>{0x1000, 0x2000, 0x8800, 0x9000, 0x40000000}));
}

// --- empty condemned = whole-heap (full GC with no bounds) → all non-survivors dropped -------------

TEST(AggregatorLifecycle, EmptyCondemnedTreatsWholeHeapAsCondemned) {
    Agg a;
    alloc(a, 0x1000);
    alloc(a, 0x2000);
    a.beginGc();
    // no condemned range reported → whole heap condemned; only survivors are kept
    a.noteSurvivorRange(0x2000, 0x8);
    a.endGc();
    EXPECT_EQ(liveAddrs(a), (std::vector<std::uint64_t>{0x2000}));
}

// --- ids are stable across moves (identity travels with the object) -------------------------------

TEST(AggregatorLifecycle, ObjectIdIsStableAcrossMoves) {
    Agg a;
    alloc(a, 0x1000);
    a.beginGc();
    a.noteCondemnedRange(0x1000, 0x8);
    a.noteSurvivorRange(0x1000, 0x8);
    a.endGc();
    auto before = a.inspectLiveObjects();
    ASSERT_EQ(before.size(), 1u);
    std::uint64_t id = before[0].id;

    // Move it; the id must follow the object to its new address.
    a.beginGc();
    a.noteCondemnedRange(0x1000, 0x8);
    a.noteMove(0x1000, 0x7000, 0x8);
    a.endGc();
    auto after = a.inspectLiveObjects();
    ASSERT_EQ(after.size(), 1u);
    EXPECT_EQ(after[0].address, 0x7000u);
    EXPECT_EQ(after[0].id, id) << "object id must be stable across a move";
}

// --- a longer randomized-ish sequence: many GCs, growth, no loss/dup, always sorted ---------------

TEST(AggregatorLifecycle, ManyGCsKeepLiveSetSortedAndConsistent) {
    Agg a;
    std::uint64_t base = 0x100000;
    for (int gc = 0; gc < 20; ++gc) {
        // allocate a fresh ephemeral batch high in the address space
        std::uint64_t ephBase = 0x10000000 + static_cast<std::uint64_t>(gc) * 0x10000;
        for (int i = 0; i < 8; ++i) alloc(a, ephBase + i * 0x20);
        a.beginGc();
        a.noteCondemnedRange(ephBase, 0x100);       // condemn only the ephemeral batch
        // half of them survive (promoted, stay in place for simplicity)
        for (int i = 0; i < 8; i += 2) a.noteSurvivorRange(ephBase + i * 0x20, 0x8);
        a.endGc();
        std::vector<std::uint64_t> live = liveAddrs(a);
        ASSERT_TRUE(std::is_sorted(live.begin(), live.end())) << "unsorted after GC " << gc;
        // no duplicate addresses
        ASSERT_EQ(std::adjacent_find(live.begin(), live.end()), live.end()) << "dup after GC " << gc;
    }
    // 20 GCs * 4 survivors each = 80 live objects retained.
    EXPECT_EQ(a.inspectLiveObjects().size(), 80u);
    (void)base;
}

TEST(AggregatorSnapshot, RepeatedProfilesAreIndependentAndParseable) {
    Aggregator aggregator(nullptr, nullptr);
    const std::filesystem::path first = tempSlab("profile-first");
    const std::filesystem::path second = tempSlab("profile-second");

    alloc(aggregator, 0x1000);
    ASSERT_TRUE(aggregator.dump(first.string()));
    alloc(aggregator, 0x2000);
    ASSERT_TRUE(aggregator.dump(second.string()));

    const std::string firstBytes = readAll(first);
    const std::string secondBytes = readAll(second);
    storage::ContainerReader firstContainer(std::as_bytes(std::span(firstBytes)));
    storage::ContainerReader secondContainer(std::as_bytes(std::span(secondBytes)));
    ASSERT_TRUE(firstContainer.valid());
    ASSERT_TRUE(secondContainer.valid());

    storage::ProvenanceReader firstProfile(firstContainer);
    storage::ProvenanceReader secondProfile(secondContainer);
    ASSERT_EQ(firstProfile.allocations().size(), 1u);
    ASSERT_EQ(secondProfile.allocations().size(), 1u);
    EXPECT_EQ(firstProfile.allocations()[0].allocCount, 1u);
    EXPECT_EQ(secondProfile.allocations()[0].allocCount, 2u);

    std::filesystem::remove(first);
    std::filesystem::remove(second);
}

TEST(AggregatorSnapshot, DumpIsSafeWhileAnotherThreadRecords) {
    Aggregator aggregator(nullptr, nullptr);
    std::atomic<std::uint64_t> recorded{0};
    std::atomic<bool> stop{false};
    std::thread writer([&] {
        while (!stop.load(std::memory_order_relaxed)) {
            std::uint64_t n = recorded.fetch_add(1, std::memory_order_relaxed);
            alloc(aggregator, 0x1000 + n * 0x20);
        }
    });
    struct WriterGuard {
        std::atomic<bool>& stop;
        std::thread& writer;
        ~WriterGuard() {
            stop.store(true, std::memory_order_relaxed);
            if (writer.joinable())
                writer.join();
        }
    } guard{stop, writer};

    while (recorded.load(std::memory_order_relaxed) < 100) {
        std::this_thread::yield();
    }

    for (int i = 0; i < 8; ++i) {
        const std::filesystem::path path = tempSlab("concurrent");
        ASSERT_TRUE(aggregator.dump(path.string()));
        const std::string bytes = readAll(path);
        storage::ContainerReader container(std::as_bytes(std::span(bytes)));
        ASSERT_TRUE(container.valid());
        storage::ProvenanceReader profile(container);
        ASSERT_EQ(profile.allocations().size(), 1u);
        EXPECT_GT(profile.allocations()[0].allocCount, 0u);
        std::filesystem::remove(path);
    }

}

TEST(AggregatorSnapshot, WriteFailureIsReportedAndLeavesNoTemporaryFile) {
    Aggregator aggregator(nullptr, nullptr);
    alloc(aggregator, 0x1000);
    const std::filesystem::path missing =
        std::filesystem::path(tempSlab("missing-parent").string() + ".missing") /
        "profile.slab";

    EXPECT_FALSE(aggregator.dump(missing.string()));
    EXPECT_FALSE(std::filesystem::exists(missing));
}
