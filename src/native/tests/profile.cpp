// Tests for the allocation-profile codec (Layer 2): allocation records reference a shared
// interned stack table, and both round-trip through the container with counters + stacks intact.

#include "sherlock/storage/profile.hpp"

#include <gtest/gtest.h>

#include <sstream>

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
    const std::uint32_t t1 = w.internType("Sherlock.Demo.Customer");
    const std::uint32_t t2 = w.internType("System.Byte[]");
    w.addAllocation(s1, t1, /*allocBytes*/ 2000, /*allocCount*/ 50, /*survivedBytes*/ 1600, /*survivedCount*/ 40);
    w.addAllocation(s2, t2, 512, 8, 0, 0);
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
    EXPECT_EQ(recs[0].typeId, t1);
    EXPECT_EQ(recs[0].allocBytes, 2000u);
    EXPECT_EQ(recs[0].allocCount, 50u);
    EXPECT_EQ(recs[0].survivedBytes, 1600u);
    EXPECT_EQ(recs[0].survivedCount, 40u);

    EXPECT_EQ(recs[1].stackId, s2);
    EXPECT_EQ(recs[1].typeId, t2);
    EXPECT_EQ(recs[1].allocBytes, 512u);

    // The typeId resolves back through the same shared table as frames.
    EXPECT_EQ(r.stacks().frame(recs[0].typeId), "Sherlock.Demo.Customer");
    EXPECT_EQ(r.stacks().frame(recs[1].typeId), "System.Byte[]");

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
    w.addAllocation(w.internStack(frames({"A"})), w.internType("T"), 100, 1, 100, 1);
    ContainerWriter cw;
    w.writeTo(cw);
    const std::string bytes = cw.finish();
    ContainerReader c(asBytes(bytes));
    EXPECT_FALSE(c.find(SectionType::Correlation).has_value());
    ProvenanceReader r(c);
    EXPECT_TRUE(r.correlation().empty());
    EXPECT_FALSE(r.stackForAddress(0x1000).has_value());
}

// With a forced tiny chunk budget, the Correlation column is emitted as several sections. The reader
// must reassemble them in order and keep the address-sorted binary search working across boundaries.
TEST(Profile, ChunkedCorrelationBinarySearchesAcrossChunks) {
    ProvenanceWriter w;
    const std::uint32_t s1 = w.internStack(frames({"A.M", "B.N"}));
    const std::uint32_t s2 = w.internStack(frames({"A.M", "C.O"}));
    // 12 objects, inserted out of order; sorted then chunked.
    for (std::uint64_t i = 12; i >= 1; --i) w.addObject(i * 0x1000, (i % 2 == 0) ? s1 : s2);
    ASSERT_EQ(w.objectCount(), 12u);

    ContainerWriter cw;
    w.writeTo(cw, /*chunkBytes*/ 48); // 3 records/chunk → 4 chunks
    ContainerReader c(asBytes(cw.finish()));
    ASSERT_TRUE(c.valid());
    ASSERT_GE(c.findAll(SectionType::Correlation).size(), 2u); // actually chunked

    ProvenanceReader r(c);
    std::span<const CorrelationRecord> corr = r.correlation();
    ASSERT_EQ(corr.size(), 12u);
    for (std::size_t i = 1; i < corr.size(); ++i)
        EXPECT_LT(corr[i - 1].address, corr[i].address); // globally sorted across chunk boundaries

    // Binary search resolves every address, including ones in non-first chunks.
    for (std::uint64_t i = 1; i <= 12; ++i) {
        auto sid = r.stackForAddress(i * 0x1000);
        ASSERT_TRUE(sid.has_value()) << "missing 0x" << std::hex << (i * 0x1000);
        EXPECT_EQ(*sid, (i % 2 == 0) ? s1 : s2);
    }
    EXPECT_FALSE(r.stackForAddress(0x1500).has_value());
}
