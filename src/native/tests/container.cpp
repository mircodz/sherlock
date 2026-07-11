// Tests for the on-disk storage container (Layer 1): header + section table + aligned typed
// sections. The GoldenBytes test pins the exact byte layout so the C++ writer and the C#
// reader can never silently drift apart (the same expected blob is asserted on both sides).

#include "sherlock/storage/container.hpp"

#include <gtest/gtest.h>

#include <cstddef>
#include <cstdint>
#include <span>
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

// The canonical fixture used by the cross-language GoldenBytes contract: one Frames section,
// version 1, blob (recordSize 0), count 2, data {1,2,3,4}. Kept identical in the C# test.
TEST(Container, GoldenBytesMatchSpec) {
    ContainerWriter w;
    const std::vector<std::uint8_t> data = {0x01, 0x02, 0x03, 0x04};
    w.addSection(SectionType::Frames, /*version*/ 1, /*recordSize*/ 0, asBytes(data), /*count*/ 2);

    const std::string bytes = w.finish();

    const std::vector<std::uint8_t> expected = {
        // header (16)
        0x53, 0x48, 0x52, 0x4B, // "SHRK"
        0x01, 0x00,             // formatVersion = 1
        0x01, 0x00,             // flags = little-endian
        0x01, 0x00, 0x00, 0x00, // sectionCount = 1
        0x00, 0x00, 0x00, 0x00, // reserved
        // section entry (32)
        0x02, 0x00, 0x00, 0x00, // type = Frames(2)
        0x01, 0x00,             // version = 1
        0x00, 0x00,             // recordSize = 0
        0x30, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // offset = 48
        0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // length = 4
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // count = 2
        // data (4)
        0x01, 0x02, 0x03, 0x04,
    };

    ASSERT_EQ(bytes.size(), expected.size());
    for (std::size_t i = 0; i < expected.size(); ++i) {
        EXPECT_EQ(static_cast<std::uint8_t>(bytes[i]), expected[i]) << "byte " << i;
    }
}

TEST(Container, RoundTripsMultipleSections) {
    ContainerWriter w;
    const std::vector<std::uint8_t> strings = {'m', 'a', 'i', 'n'};
    const std::vector<std::uint32_t> recs = {10, 20, 30};
    w.addSection(SectionType::Strings, 1, 0, asBytes(strings), strings.size());
    w.addRecords<std::uint32_t>(SectionType::Allocations, 2, recs);

    const std::string bytes = w.finish();
    ContainerReader r(asBytes(bytes));

    ASSERT_TRUE(r.valid());
    EXPECT_EQ(r.version(), kFormatVersion);
    ASSERT_EQ(r.sections().size(), 2u);

    auto str = r.find(SectionType::Strings);
    ASSERT_TRUE(str.has_value());
    EXPECT_EQ(str->version, 1);
    EXPECT_EQ(str->recordSize, 0);
    EXPECT_EQ(str->count, 4u);
    EXPECT_EQ(str->data.size(), 4u);

    auto alloc = r.find(SectionType::Allocations);
    ASSERT_TRUE(alloc.has_value());
    EXPECT_EQ(alloc->version, 2);
    EXPECT_EQ(alloc->recordSize, sizeof(std::uint32_t));
    std::span<const std::uint32_t> got = alloc->records<std::uint32_t>();
    ASSERT_EQ(got.size(), 3u);
    EXPECT_EQ(got[0], 10u);
    EXPECT_EQ(got[1], 20u);
    EXPECT_EQ(got[2], 30u);
}

TEST(Container, SectionsAreEightAligned) {
    ContainerWriter w;
    const std::vector<std::uint8_t> odd = {1, 2, 3}; // 3 bytes → next section must still align
    w.addSection(SectionType::Strings, 1, 0, asBytes(odd), 3);
    w.addSection(SectionType::Frames, 1, 0, asBytes(odd), 3);

    const std::string bytes = w.finish();
    ContainerReader r(asBytes(bytes));
    ASSERT_TRUE(r.valid());
    for (const SectionView& s : r.sections()) {
        const auto off = static_cast<std::size_t>(s.data.data() - reinterpret_cast<const std::byte*>(bytes.data()));
        EXPECT_EQ(off % kAlignment, 0u);
    }
}

TEST(Container, EmptyContainerIsJustHeader) {
    ContainerWriter w;
    const std::string bytes = w.finish();
    EXPECT_EQ(bytes.size(), kHeaderSize);
    ContainerReader r(asBytes(bytes));
    EXPECT_TRUE(r.valid());
    EXPECT_TRUE(r.sections().empty());
}

TEST(Container, RejectsBadMagicAndTruncation) {
    std::string bad = "NOPE............";
    EXPECT_FALSE(ContainerReader(asBytes(bad)).valid());

    std::string tooSmall = "SHR";
    EXPECT_FALSE(ContainerReader(asBytes(tooSmall)).valid());
}
