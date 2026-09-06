// Drive allocation/GC lifecycles without CLR metadata to verify live addresses and identities.

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

// Dummy metadata keeps these tests focused on address bookkeeping.
constexpr FrameId kFrame = 0x1000;
constexpr ClassID kClass = 0x2000;

MethodRegistry& testMethods() {
    static MethodRegistry methods(nullptr, nullptr);
    return methods;
}

void alloc(Aggregator& a, std::uint64_t addr, std::uint64_t bytes = 24) {
    FrameId frames[1] = {kFrame};
    a.record(std::span<const FrameId>(frames, 1), bytes, static_cast<ObjectID>(addr), kClass);
}

std::vector<std::uint64_t> liveAddrs(const Aggregator& a) {
    std::vector<std::uint64_t> v;
    for (const Aggregator::LiveObjectInfo& object : a.inspectLiveObjects()) {
        v.push_back(object.address);
    }
    return v;
}

struct Agg : Aggregator {
    Agg() : Aggregator(nullptr, nullptr, testMethods()) { enableCorrelation(); }
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

TEST(AggregatorLifecycle, PromotedObjectSurvivesEphemeralGC) {
    Agg a;
    alloc(a, 0x1000);                        // will be "promoted" (low address = old gen)
    // Admit the object before an unrelated ephemeral collection.
    a.beginGc();
    a.noteCondemnedRange(0x1000, 0x8);
    a.noteSurvivorRange(0x1000, 0x8);
    a.endGc();
    ASSERT_EQ(liveAddrs(a), (std::vector<std::uint64_t>{0x1000}));

    // 0x1000 is uncondemned and has no survivor report; retain it.
    alloc(a, 0x8000);
    a.beginGc();
    a.noteCondemnedRange(0x8000, 0x1000);
    a.noteSurvivorRange(0x8000, 0x8);        // the gen-0 object survives
    a.endGc();
    EXPECT_EQ(liveAddrs(a), (std::vector<std::uint64_t>{0x1000, 0x8000}));
}

TEST(AggregatorLifecycle, LargeObjectAdmittedWhenNotCondemned) {
    Agg a;
    // Ephemeral GC must admit the unexamined LOH object without a survivor report.
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
    // Cross-heap moves reverse block order; the live set must remain globally sorted.
    a.noteMove(0x1000, 0x9000, 0x8);
    a.noteMove(0x1008, 0x9008, 0x8);
    a.noteMove(0x2000, 0x8000, 0x8);
    a.noteMove(0x2008, 0x8008, 0x8);
    a.endGc();
    std::vector<std::uint64_t> live = liveAddrs(a);
    EXPECT_TRUE(std::is_sorted(live.begin(), live.end())) << "live set must stay globally sorted";
    EXPECT_EQ(live, (std::vector<std::uint64_t>{0x8000, 0x8008, 0x9000, 0x9008}));
}

TEST(AggregatorLifecycle, WindowedUpdatePreservesPrefixAndSuffix) {
    Agg a;
    // Admit objects across three address regions.
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

    // Change only [0x8000,0x9008); preserve the prefix and LOH suffix.
    alloc(a, 0x8800);   // a new gen-0 object in the window that survives
    a.beginGc();
    a.noteCondemnedRange(0x8000, 0x1008);   // [0x8000, 0x9008)
    a.noteSurvivorRange(0x9000, 0x8);
    a.noteSurvivorRange(0x8800, 0x8);
    a.endGc();
    EXPECT_EQ(liveAddrs(a),
              (std::vector<std::uint64_t>{0x1000, 0x2000, 0x8800, 0x9000, 0x40000000}));
}

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

TEST(AggregatorLifecycle, ManyGCsKeepLiveSetSortedAndConsistent) {
    Agg a;
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
}

TEST(AggregatorSnapshot, RepeatedProfilesAreIndependentAndParseable) {
    Aggregator aggregator(nullptr, nullptr, testMethods());
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
    Aggregator aggregator(nullptr, nullptr, testMethods());
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
    Aggregator aggregator(nullptr, nullptr, testMethods());
    alloc(aggregator, 0x1000);
    const std::filesystem::path missing =
        std::filesystem::path(tempSlab("missing-parent").string() + ".missing") /
        "profile.slab";

    EXPECT_FALSE(aggregator.dump(missing.string()));
    EXPECT_FALSE(std::filesystem::exists(missing));
}

TEST(AggregatorSnapshot, DistinctMethodCookiesRemainDistinctInTheSlab) {
    MethodRegistry methods([](ModuleID, mdMethodDef) {
        return MethodRegistry::Resolution{"Example.Generic<T>.Allocate", S_OK, {}};
    });
    methods.moduleLoaded(0x1000);
    FrameId first = methods.intern(0x1000, 0x06000001);
    FrameId overload = methods.intern(0x1000, 0x06000002);
    const std::string firstName = methods.name(first);
    const std::string overloadName = methods.name(overload);
    Aggregator aggregator(nullptr, nullptr, methods);
    aggregator.record(std::span<const FrameId>(&first, 1), 24, 0x2000, kClass);
    aggregator.record(std::span<const FrameId>(&overload, 1), 24, 0x3000, kClass);
    methods.moduleUnloaded(0x1000);

    const std::filesystem::path path = tempSlab("method-cookies");
    ASSERT_TRUE(aggregator.dump(path.string()));
    const std::string bytes = readAll(path);
    storage::ContainerReader container(std::as_bytes(std::span(bytes)));
    ASSERT_TRUE(container.valid());
    storage::ProvenanceReader profile(container);
    storage::StackTable stacks = storage::StackTable::read(container);
    ASSERT_EQ(profile.allocations().size(), 2);
    std::vector<std::string> names;
    for (const storage::AllocationRecord& record : profile.allocations()) {
        auto frames = stacks.stackFrames(record.stackId);
        ASSERT_EQ(frames.size(), 1);
        names.emplace_back(stacks.frame(frames[0]));
    }
    std::sort(names.begin(), names.end());
    std::vector<std::string> expected = {firstName, overloadName};
    std::sort(expected.begin(), expected.end());
    EXPECT_EQ(names, expected);
    EXPECT_NE(names[0], names[1]);
    std::filesystem::remove(path);
}
