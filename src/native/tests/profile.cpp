// Tests for the allocation-profile codec (Layer 2): allocation records reference a shared
// interned stack table, and both round-trip through the container with counters + stacks intact.

#include "sherlock/storage/profile.hpp"

#include <gtest/gtest.h>

#include <cstddef>
#include <cstdint>
#include <span>
#include <string>
#include <string_view>
#include <vector>

using namespace Sherlock::storage;

namespace {
std::span<const std::byte> asBytes(const std::string& s) {
    return {reinterpret_cast<const std::byte*>(s.data()), s.size()};
}
std::span<const std::string_view> frames(const std::vector<std::string_view>& v) { return {v.data(), v.size()}; }
} // namespace

TEST(Profile, RoundTripsRecordsAndStacks) {
    ProvenanceWriter w;
    const std::uint32_t s1 = w.internStack(frames({"Program.Main", "Registry.Add"}));
    const std::uint32_t s2 = w.internStack(frames({"Program.Main", "List.Resize"}));
    w.addAllocation(s1, /*allocBytes*/ 2000, /*allocCount*/ 50, /*survivedBytes*/ 1600, /*survivedCount*/ 40);
    w.addAllocation(s2, 512, 8, 0, 0);
    ASSERT_EQ(w.allocationCount(), 2u);

    ContainerWriter cw;
    w.writeTo(cw);
    const std::string bytes = cw.finish(); // keep the buffer alive; the reader borrows it
    ContainerReader c(asBytes(bytes));
    ASSERT_TRUE(c.valid());

    ProvenanceReader r(c);
    std::span<const AllocationRecord> recs = r.allocations();
    ASSERT_EQ(recs.size(), 2u);

    EXPECT_EQ(recs[0].stackId, s1);
    EXPECT_EQ(recs[0].allocBytes, 2000u);
    EXPECT_EQ(recs[0].allocCount, 50u);
    EXPECT_EQ(recs[0].survivedBytes, 1600u);
    EXPECT_EQ(recs[0].survivedCount, 40u);

    EXPECT_EQ(recs[1].stackId, s2);
    EXPECT_EQ(recs[1].allocBytes, 512u);

    // The record's stackId resolves back through the shared table to the original frames.
    std::span<const std::uint32_t> f1 = r.stacks().stackFrames(recs[0].stackId);
    ASSERT_EQ(f1.size(), 2u);
    EXPECT_EQ(r.stacks().frame(f1[0]), "Program.Main");
    EXPECT_EQ(r.stacks().frame(f1[1]), "Registry.Add");
}

TEST(Profile, SharesOneStackAcrossSites) {
    // Two sites with the same stack must reference the same stackId (shared identity space).
    ProvenanceWriter w;
    const std::uint32_t a = w.internStack(frames({"A", "B"}));
    const std::uint32_t b = w.internStack(frames({"A", "B"}));
    EXPECT_EQ(a, b);
}

TEST(Profile, CorrelationIsSortedAndBinarySearchable) {
    ProvenanceWriter w;
    const std::uint32_t s1 = w.internStack(frames({"Program.Main", "Registry.Add"}));
    const std::uint32_t s2 = w.internStack(frames({"Program.Main", "List.Resize"}));
    // Insert out of address order; the writer must sort so the reader can binary-search.
    w.addObject(0x3000, s2);
    w.addObject(0x1000, s1);
    w.addObject(0x2000, s1);
    ASSERT_EQ(w.objectCount(), 3u);

    ContainerWriter cw;
    w.writeTo(cw);
    const std::string bytes = cw.finish();
    ContainerReader c(asBytes(bytes));
    ASSERT_TRUE(c.valid());

    ProvenanceReader r(c);
    std::span<const CorrelationRecord> corr = r.correlation();
    ASSERT_EQ(corr.size(), 3u);
    EXPECT_EQ(corr[0].address, 0x1000u); // sorted ascending
    EXPECT_EQ(corr[1].address, 0x2000u);
    EXPECT_EQ(corr[2].address, 0x3000u);

    // Address -> allocating stack, resolved through the shared table.
    auto sid = r.stackForAddress(0x2000);
    ASSERT_TRUE(sid.has_value());
    EXPECT_EQ(*sid, s1);
    std::span<const std::uint32_t> f = r.stacks().stackFrames(*sid);
    ASSERT_EQ(f.size(), 2u);
    EXPECT_EQ(r.stacks().frame(f[1]), "Registry.Add");

    EXPECT_EQ(r.stackForAddress(0x3000).value(), s2);
    EXPECT_FALSE(r.stackForAddress(0x1500).has_value()); // untracked address
}

TEST(Profile, NoCorrelationSectionWhenAggregateOnly) {
    // The exit-time aggregate has allocations but no per-object correlation.
    ProvenanceWriter w;
    w.addAllocation(w.internStack(frames({"A"})), 100, 1, 100, 1);
    ContainerWriter cw;
    w.writeTo(cw);
    const std::string bytes = cw.finish();
    ContainerReader c(asBytes(bytes));
    EXPECT_FALSE(c.find(SectionType::Correlation).has_value());
    ProvenanceReader r(c);
    EXPECT_TRUE(r.correlation().empty());
    EXPECT_FALSE(r.stackForAddress(0x1000).has_value());
}
