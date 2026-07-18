// Tests for the on-disk storage container (Layer 1): header + section table + aligned typed
// sections. The GoldenBytes test pins the exact byte layout so the C++ writer and the C#
// reader can never silently drift apart (the same expected blob is asserted on both sides).

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

// addChunkedRecords splits a fixed-width column into N same-typed sections of a uniform element
// count (last is short), so the C# reader can map each chunk under its ~2 GB section cap and index
// element i arithmetically. Forced tiny chunkBytes here to exercise the multi-chunk path.
TEST(Container, ChunkedRecordsSplitUniformlyAndReassemble) {
    struct Rec { std::uint64_t a; std::uint64_t b; }; // 16 bytes
    std::vector<Rec> recs;
    for (std::uint64_t i = 0; i < 10; ++i) recs.push_back({i, i * 100});

    ContainerWriter w;
    // 48 bytes/chunk = 3 records/chunk → 10 records => 4 sections (3,3,3,1).
    w.addChunkedRecords<Rec>(SectionType::Correlation, /*version*/ 2, recs, /*chunkBytes*/ 48);

    const std::string bytes = w.finish();
    ContainerReader r(asBytes(bytes));
    ASSERT_TRUE(r.valid());

    std::vector<SectionView> chunks = r.findAll(SectionType::Correlation);
    ASSERT_EQ(chunks.size(), 4u);
    EXPECT_EQ(chunks[0].count, 3u);
    EXPECT_EQ(chunks[1].count, 3u);
    EXPECT_EQ(chunks[2].count, 3u);
    EXPECT_EQ(chunks[3].count, 1u); // short final chunk
    for (const SectionView& s : chunks) {
        EXPECT_EQ(s.version, 2u);
        EXPECT_EQ(s.recordSize, sizeof(Rec));
    }

    // Reassemble in table order and check element-for-element (global order preserved across chunks).
    std::vector<Rec> got;
    for (const SectionView& s : chunks) {
        std::span<const Rec> c = s.records<Rec>();
        got.insert(got.end(), c.begin(), c.end());
    }
    ASSERT_EQ(got.size(), recs.size());
    for (std::size_t i = 0; i < recs.size(); ++i) {
        EXPECT_EQ(got[i].a, recs[i].a);
        EXPECT_EQ(got[i].b, recs[i].b);
    }
}

TEST(Container, ChunkedRecordsSingleChunkAndEmpty) {
    struct Rec { std::uint32_t x; };
    // Fits one chunk → exactly one section.
    std::vector<Rec> one = {{1}, {2}};
    ContainerWriter w;
    w.addChunkedRecords<Rec>(SectionType::Allocations, 1, one, /*chunkBytes*/ 1024);
    ContainerReader r(asBytes(w.finish()));
    EXPECT_EQ(r.findAll(SectionType::Allocations).size(), 1u);

    // Empty column → zero sections.
    ContainerWriter w2;
    w2.addChunkedRecords<Rec>(SectionType::Allocations, 1, std::span<const Rec>{});
    ContainerReader r2(asBytes(w2.finish()));
    EXPECT_TRUE(r2.findAll(SectionType::Allocations).empty());
}

// writeTo(ostream) must be byte-identical to finish() for a chunked (multi-section) container too.
TEST(Container, ChunkedWriteToMatchesFinish) {
    struct Rec { std::uint64_t a; std::uint64_t b; };
    std::vector<Rec> recs;
    for (std::uint64_t i = 0; i < 7; ++i) recs.push_back({i, ~i});
    ContainerWriter w;
    w.addChunkedRecords<Rec>(SectionType::Correlation, 2, recs, /*chunkBytes*/ 32); // 2 recs/chunk

    const std::string viaFinish = w.finish();
    std::ostringstream os;
    ASSERT_TRUE(w.writeTo(os));
    EXPECT_EQ(os.str(), viaFinish);
}
