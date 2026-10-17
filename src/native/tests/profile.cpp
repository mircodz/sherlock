// Allocation and correlation records resolve through the same symbol table.

#include "sherlock/storage/profile.hpp"

#include <gtest/gtest.h>

#include <sstream>

#include <cstddef>
#include <cstdint>
#include <functional>
#include <initializer_list>
#include <span>
#include <string>
#include <string_view>
#include <vector>

using namespace Sherlock::storage;

namespace {
std::span<const std::byte> asBytes(const std::string& s) {
    return {reinterpret_cast<const std::byte*>(s.data()), s.size()};
}
// Interns "Type.Method" names as methods of one module, then their stack; equal names are one method.
std::uint32_t stack(ProvenanceWriter& w, std::initializer_list<std::string_view> names) {
    std::vector<std::uint32_t> methods;
    for (std::string_view name : names) {
        MethodDescription m;
        m.mvid = {0x42};
        m.token = 0x06000000u | static_cast<std::uint32_t>(std::hash<std::string_view>{}(name) & 0xffffff);
        m.module = "App.dll";
        std::size_t dot = name.rfind('.');
        m.type = dot == std::string_view::npos ? std::string_view() : name.substr(0, dot);
        m.name = dot == std::string_view::npos ? name : name.substr(dot + 1);
        m.parameters = m.qualifiedParameters = "()";
        methods.push_back(w.internMethod(m));
    }
    return w.internStack(methods);
}

std::string method(const SymbolTable& symbols, std::uint32_t index) {
    const MethodRecord& m = symbols.method(index);
    return std::string(symbols.text(m.type)) + "." + std::string(symbols.text(m.name));
}
} // namespace

TEST(Profile, RoundTripsRecordsAndStacks) {
    ProvenanceWriter w;
    const std::uint32_t s1 = stack(w, {"Program.Main", "Registry.Add"});
    const std::uint32_t s2 = stack(w, {"Program.Main", "List.Resize"});
    const std::uint32_t t1 = w.internType("Sherlock.Demo.Customer");
    const std::uint32_t t2 = w.internType("System.Byte[]");
    w.addAllocation(s1, t1, /*allocBytes*/ 2000, /*allocCount*/ 50, /*survivedBytes*/ 1600, /*survivedCount*/ 40);
    w.addAllocation(s2, t2, 512, 8, 0, 0);
    ASSERT_EQ(w.allocationCount(), 2u);

    ContainerWriter cw(SlabKind::Profile, kProfileVersion);
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

    // Types and frames share the symbol table.
    EXPECT_EQ(r.symbols().type(recs[0].typeId), "Sherlock.Demo.Customer");
    EXPECT_EQ(r.symbols().type(recs[1].typeId), "System.Byte[]");

    std::span<const std::uint32_t> f1 = r.symbols().stackFrames(recs[0].stackId);
    ASSERT_EQ(f1.size(), 2u);
    EXPECT_EQ(method(r.symbols(), f1[0]), "Program.Main");
    EXPECT_EQ(method(r.symbols(), f1[1]), "Registry.Add");
}

TEST(Profile, SharesOneStackAcrossSites) {
    ProvenanceWriter w;
    const std::uint32_t a = stack(w, {"A", "B"});
    const std::uint32_t b = stack(w, {"A", "B"});
    EXPECT_EQ(a, b);
}

TEST(Profile, CorrelationIsSortedAndBinarySearchable) {
    ProvenanceWriter w;
    const std::uint32_t s1 = stack(w, {"Program.Main", "Registry.Add"});
    const std::uint32_t s2 = stack(w, {"Program.Main", "List.Resize"});
    // Insert out of address order; the writer must sort so the reader can binary-search.
    w.addObject(0x3000, s2);
    w.addObject(0x1000, s1);
    w.addObject(0x2000, s1);
    ASSERT_EQ(w.objectCount(), 3u);

    ContainerWriter cw(SlabKind::Profile, kProfileVersion);
    w.writeTo(cw);
    const std::string bytes = cw.finish();
    ContainerReader c(asBytes(bytes));
    ASSERT_TRUE(c.valid());

    ProvenanceReader r(c);
    std::span<const std::uint64_t> addresses = r.objectAddresses();
    ASSERT_EQ(addresses.size(), 3u);
    ASSERT_EQ(r.objectStacks().size(), 3u);
    EXPECT_EQ(addresses[0], 0x1000u); // sorted ascending, stacks moved with them
    EXPECT_EQ(addresses[1], 0x2000u);
    EXPECT_EQ(addresses[2], 0x3000u);
    EXPECT_EQ(r.objectStacks()[0], s1);
    EXPECT_EQ(r.objectStacks()[2], s2);

    // Address -> allocating stack, resolved through the shared table.
    auto sid = r.stackForAddress(0x2000);
    ASSERT_TRUE(sid.has_value());
    EXPECT_EQ(*sid, s1);
    std::span<const std::uint32_t> f = r.symbols().stackFrames(*sid);
    ASSERT_EQ(f.size(), 2u);
    EXPECT_EQ(method(r.symbols(), f[1]), "Registry.Add");

    EXPECT_EQ(r.stackForAddress(0x3000).value(), s2);
    EXPECT_FALSE(r.stackForAddress(0x1500).has_value()); // untracked address
}

TEST(Profile, NoCorrelationSectionWhenAggregateOnly) {
    // The exit-time aggregate has allocations but no per-object correlation.
    ProvenanceWriter w;
    w.addAllocation(stack(w, {"A"}), w.internType("T"), 100, 1, 100, 1);
    ContainerWriter cw(SlabKind::Profile, kProfileVersion);
    w.writeTo(cw);
    const std::string bytes = cw.finish();
    ContainerReader c(asBytes(bytes));
    EXPECT_FALSE(c.find(SectionType::ObjectAddresses).has_value());
    EXPECT_FALSE(c.find(SectionType::ObjectStacks).has_value());
    ProvenanceReader r(c);
    EXPECT_TRUE(r.objectAddresses().empty());
    EXPECT_FALSE(r.stackForAddress(0x1000).has_value());
}

TEST(Profile, CorrelationIsSortedForBinarySearch) {
    ProvenanceWriter w;
    const std::uint32_t s1 = stack(w, {"A.M", "B.N"});
    const std::uint32_t s2 = stack(w, {"A.M", "C.O"});
    // 12 objects, inserted out of order.
    for (std::uint64_t i = 12; i >= 1; --i) w.addObject(i * 0x1000, (i % 2 == 0) ? s1 : s2);
    ASSERT_EQ(w.objectCount(), 12u);

    ContainerWriter cw(SlabKind::Profile, kProfileVersion);
    w.writeTo(cw);
    const std::string bytes = cw.finish();
    ContainerReader c(asBytes(bytes));
    ASSERT_TRUE(c.valid());

    ProvenanceReader r(c);
    std::span<const std::uint64_t> addresses = r.objectAddresses();
    ASSERT_EQ(addresses.size(), 12u);
    for (std::size_t i = 1; i < addresses.size(); ++i)
        EXPECT_LT(addresses[i - 1], addresses[i]);

    for (std::uint64_t i = 1; i <= 12; ++i) {
        auto sid = r.stackForAddress(i * 0x1000);
        ASSERT_TRUE(sid.has_value()) << "missing 0x" << std::hex << (i * 0x1000);
        EXPECT_EQ(*sid, (i % 2 == 0) ? s1 : s2);
    }
    EXPECT_FALSE(r.stackForAddress(0x1500).has_value());
}
