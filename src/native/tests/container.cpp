// GoldenBytes pins the same container layout in C++ and C#.

#include "sherlock/storage/container.hpp"

#include <gtest/gtest.h>

#include <cstddef>
#include <cstdint>
#include <span>
#include <sstream>
#include <string>
#include <vector>

using namespace Sherlock::storage;

namespace {

std::span<const std::byte> asBytes(const std::string& s) {
    return {reinterpret_cast<const std::byte*>(s.data()), s.size()};
}

std::span<const std::byte> asBytes(const std::vector<std::uint8_t>& v) {
    return {reinterpret_cast<const std::byte*>(v.data()), v.size()};
}

} // namespace

// Keep this one-section fixture identical to the C# GoldenBytes test.
TEST(Container, GoldenBytesMatchSpec) {
    ContainerWriter w(SlabKind::Profile, 7);
    const std::vector<std::uint8_t> data = {0x01, 0x02, 0x03, 0x04};
    w.addBytes(SectionType::Strings, asBytes(data));

    const std::string bytes = w.finish();

    const std::vector<std::uint8_t> head = {
        0x53, 0x48, 0x52, 0x4B, // "SHRK"
        0x02, 0x00,             // container version = 2
        0x01, 0x00,             // kind = Profile
        0x07, 0x00, 0x00, 0x00, // kind version = 7
        0x01, 0x00, 0x00, 0x00, // section count = 1
        0x01, 0x00, 0x00, 0x00, // type = Strings
        0x01, 0x00, 0x00, 0x00, // record size = 1
        0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // offset = 4096
        0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // length = 4
    };
    ASSERT_EQ(bytes.size(), 4096u + 4u);
    for (std::size_t i = 0; i < head.size(); ++i) {
        EXPECT_EQ(static_cast<std::uint8_t>(bytes[i]), head[i]) << "byte " << i;
    }
    for (std::size_t i = head.size(); i < 4096; ++i) {
        ASSERT_EQ(bytes[i], '\0') << "padding byte " << i;
    }
    EXPECT_EQ(bytes.substr(4096), std::string("\x01\x02\x03\x04", 4));
}

TEST(Container, RoundTripsMultipleSections) {
    ContainerWriter w(SlabKind::Profile, 3);
    const std::vector<std::uint8_t> strings = {'m', 'a', 'i', 'n'};
    const std::vector<std::uint32_t> recs = {10, 20, 30};
    w.addBytes(SectionType::Strings, asBytes(strings));
    w.addRecords<std::uint32_t>(SectionType::Allocations, recs);

    const std::string bytes = w.finish();
    ContainerReader r(asBytes(bytes));

    ASSERT_TRUE(r.valid());
    EXPECT_EQ(r.kind(), SlabKind::Profile);
    EXPECT_EQ(r.kindVersion(), 3u);
    ASSERT_EQ(r.sections().size(), 2u);

    auto str = r.find(SectionType::Strings);
    ASSERT_TRUE(str.has_value());
    EXPECT_EQ(str->recordSize, 1u);
    EXPECT_EQ(str->data.size(), 4u);

    auto alloc = r.find(SectionType::Allocations);
    ASSERT_TRUE(alloc.has_value());
    EXPECT_EQ(alloc->recordSize, sizeof(std::uint32_t));
    std::span<const std::uint32_t> got = alloc->records<std::uint32_t>();
    ASSERT_EQ(got.size(), 3u);
    EXPECT_EQ(got[0], 10u);
    EXPECT_EQ(got[1], 20u);
    EXPECT_EQ(got[2], 30u);
}

TEST(Container, SectionsStartOnPageBoundaries) {
    ContainerWriter w(SlabKind::Profile, 1);
    const std::vector<std::uint8_t> odd = {1, 2, 3}; // 3 bytes, so the next section must still align
    w.addBytes(SectionType::Strings, asBytes(odd));
    w.addBytes(SectionType::Types, asBytes(odd));

    const std::string bytes = w.finish();
    ContainerReader r(asBytes(bytes));
    ASSERT_TRUE(r.valid());
    for (const SectionView& s : r.sections()) {
        const auto off = static_cast<std::size_t>(s.data.data() - reinterpret_cast<const std::byte*>(bytes.data()));
        EXPECT_EQ(off % kAlignment, 0u);
    }
}

TEST(Container, EmptyContainerIsJustHeader) {
    ContainerWriter w(SlabKind::Profile, 1);
    const std::string bytes = w.finish();
    EXPECT_EQ(bytes.size(), kHeaderSize);
    ContainerReader r(asBytes(bytes));
    EXPECT_TRUE(r.valid());
    EXPECT_TRUE(r.sections().empty());
}

TEST(Container, RejectsBadMagicTruncationAndOtherVersions) {
    std::string bad = "NOPE............";
    EXPECT_FALSE(ContainerReader(asBytes(bad)).valid());

    std::string tooSmall = "SHR";
    EXPECT_FALSE(ContainerReader(asBytes(tooSmall)).valid());

    std::string older = ContainerWriter(SlabKind::Profile, 1).finish();
    older[4] = 0x01; // container version 1
    EXPECT_FALSE(ContainerReader(asBytes(older)).valid());
}

TEST(Container, WriteToMatchesFinish) {
    ContainerWriter w(SlabKind::Profile, 1);
    const std::vector<std::uint64_t> recs = {1, 2, 3, 4, 5, 6, 7};
    w.addRecords<std::uint64_t>(SectionType::Correlation, recs);
    w.addBytes(SectionType::Strings, asBytes(std::string("abc")));

    const std::string viaFinish = w.finish();
    std::ostringstream os;
    ASSERT_TRUE(w.writeTo(os));
    EXPECT_EQ(os.str(), viaFinish);
}
