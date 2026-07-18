#pragma once

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <optional>
#include <ostream>
#include <span>
#include <string>
#include <type_traits>
#include <vector>

// The on-disk container: a fixed header + a section table + 8-byte-aligned typed sections. 
// Sections are opaque blobs here; payload codecs layer on top.
// Scalars are encoded little-endian explicitly, so the format is independent of struct packing and
// host endianness. Mirrors the C# Sherlock.Core.Storage types.
namespace Sherlock::storage {

inline constexpr char kMagic[4] = {'S', 'H', 'R', 'K'};
inline constexpr std::uint16_t kFormatVersion = 1;
inline constexpr std::uint16_t kFlagLittleEndian = 0x1;

inline constexpr std::size_t kHeaderSize = 16;
inline constexpr std::size_t kSectionEntrySize = 32;
inline constexpr std::size_t kAlignment = 8;

// Default max bytes per chunk when splitting a large fixed-width column (addChunkedRecords). Stays
// well under the C# reader's ~2 GB single-section cap; a multiple of 8 so no 8/4/2-byte record
// straddles a chunk boundary.
inline constexpr std::size_t kDefaultChunkBytes = 256u << 20; // 256 MiB

/// Section kinds. The container layer treats these as opaque; the payload meaning is defined
/// by the Layer-2 codecs. Kept in sync with the C# SectionType enum.
enum class SectionType : std::uint32_t {
    Strings = 1,
    Frames = 2,
    Stacks = 3,
    StackFrames = 4,
    Allocations = 5,
    Correlation = 6,
};

namespace detail {

inline void putU16(std::string& b, std::uint16_t v) {
    b.push_back(static_cast<char>(v & 0xFF));
    b.push_back(static_cast<char>((v >> 8) & 0xFF));
}

inline void putU32(std::string& b, std::uint32_t v) {
    for (int i = 0; i < 4; ++i) {
        b.push_back(static_cast<char>((v >> (8 * i)) & 0xFF));
    }
}

inline void putU64(std::string& b, std::uint64_t v) {
    for (int i = 0; i < 8; ++i) {
        b.push_back(static_cast<char>((v >> (8 * i)) & 0xFF));
    }
}

inline std::uint16_t getU16(const std::uint8_t* p) {
    return static_cast<std::uint16_t>(p[0]) | static_cast<std::uint16_t>(p[1] << 8);
}

inline std::uint32_t getU32(const std::uint8_t* p) {
    std::uint32_t v = 0;
    for (int i = 0; i < 4; ++i) {
        v |= static_cast<std::uint32_t>(p[i]) << (8 * i);
    }
    return v;
}

inline std::uint64_t getU64(const std::uint8_t* p) {
    std::uint64_t v = 0;
    for (int i = 0; i < 8; ++i) {
        v |= static_cast<std::uint64_t>(p[i]) << (8 * i);
    }
    return v;
}

inline std::size_t alignUp(std::size_t n, std::size_t a) { return (n + a - 1) & ~(a - 1); }

} // namespace detail

/// Accumulates typed sections and serializes the container. Sections are emitted in the order
/// added; each starts at an 8-aligned offset. There is no trailing padding.
class ContainerWriter {
public:
    /// Adds a section of raw bytes. `recordSize` is bytes/record for fixed-width record
    /// sections (0 for variable/blob sections); `count` is the record/entry count.
    void addSection(SectionType type, std::uint16_t version, std::uint16_t recordSize,
                    std::span<const std::byte> data, std::uint64_t count) {
        Section s;
        s.type = type;
        s.version = version;
        s.recordSize = recordSize;
        s.count = count;
        s.data.assign(reinterpret_cast<const char*>(data.data()), data.size());
        sections_.push_back(std::move(s));
    }

    /// Adds a fixed-width record section from a contiguous array of trivially-copyable `T`.
    template <typename T>
    void addRecords(SectionType type, std::uint16_t version, std::span<const T> records) {
        static_assert(std::is_trivially_copyable_v<T>, "record type must be trivially copyable");
        addSection(type, version, static_cast<std::uint16_t>(sizeof(T)), std::as_bytes(records), records.size());
    }

    /// Adds a fixed-width column as one or more same-typed sections, each at most `chunkBytes` bytes.
    /// The C# reader caps a single section at ~2 GB (int-length span), so a large column (e.g. one
    /// CorrelationRecord per live object) must be split. Every chunk but the last holds exactly
    /// `chunkBytes / sizeof(T)` records — a uniform count the reader relies on to index element `i`
    /// arithmetically (chunk = i / elemsPerChunk), so no chunk-start table is needed. The sort (if any)
    /// must already be applied: chunking is a pure post-sort partition preserving global order.
    template <typename T>
    void addChunkedRecords(SectionType type, std::uint16_t version, std::span<const T> records,
                           std::size_t chunkBytes = kDefaultChunkBytes) {
        static_assert(std::is_trivially_copyable_v<T>, "record type must be trivially copyable");
        const std::size_t elemsPerChunk = std::max<std::size_t>(1, chunkBytes / sizeof(T));
        for (std::size_t i = 0; i < records.size(); i += elemsPerChunk) {
            std::size_t n = std::min(elemsPerChunk, records.size() - i);
            addSection(type, version, static_cast<std::uint16_t>(sizeof(T)),
                       std::as_bytes(records.subspan(i, n)), n);
        }
    }

    /// Serializes the whole container into a byte buffer. For small/in-memory containers and tests;
    /// large containers should use writeTo(ostream) to avoid a second full copy in RAM.
    [[nodiscard]] std::string finish() const {
        std::string out;
        out.reserve(computeLayout());
        appendHeaderAndTable([&out](const char* p, std::size_t n) { out.append(p, n); });
        for (std::size_t i = 0; i < sections_.size(); ++i) {
            while (out.size() < offsetOf(i)) out.push_back('\0');
            out.append(sections_[i].data);
        }
        return out;
    }

    /// Streams the container to `os` without materializing the whole file — peak RAM is the largest
    /// single section, not the sum. Byte-identical to finish(). Returns os.good().
    bool writeTo(std::ostream& os) const {
        std::string head;
        head.reserve(kHeaderSize + kSectionEntrySize * sections_.size());
        appendHeaderAndTable([&head](const char* p, std::size_t n) { head.append(p, n); });
        os.write(head.data(), static_cast<std::streamsize>(head.size()));

        static constexpr char kPad[kAlignment] = {};
        std::size_t pos = head.size();
        for (std::size_t i = 0; i < sections_.size(); ++i) {
            std::size_t off = offsetOf(i);
            if (pos < off) {
                os.write(kPad, static_cast<std::streamsize>(off - pos));
                pos = off;
            }
            os.write(sections_[i].data.data(), static_cast<std::streamsize>(sections_[i].data.size()));
            pos += sections_[i].data.size();
        }
        return static_cast<bool>(os);
    }

private:
    struct Section {
        SectionType type;
        std::uint16_t version;
        std::uint16_t recordSize;
        std::uint64_t count;
        std::string data;
    };
    std::vector<Section> sections_;

    // Aligned byte offset of section i's data. O(i), but the section count is tiny (≤~10).
    std::size_t offsetOf(std::size_t i) const {
        std::size_t cursor = detail::alignUp(kHeaderSize + kSectionEntrySize * sections_.size(), kAlignment);
        for (std::size_t k = 0; k < i; ++k) {
            cursor = detail::alignUp(cursor + sections_[k].data.size(), kAlignment);
        }
        return cursor;
    }

    // Total container size in bytes.
    std::size_t computeLayout() const {
        if (sections_.empty()) return kHeaderSize;
        return offsetOf(sections_.size() - 1) + sections_.back().data.size();
    }

    // Header + section table, emitted via sink(ptr, len). Shared by finish() and writeTo().
    template <typename Sink>
    void appendHeaderAndTable(Sink sink) const {
        std::string hdr;
        hdr.append(kMagic, 4);
        detail::putU16(hdr, kFormatVersion);
        detail::putU16(hdr, kFlagLittleEndian);
        detail::putU32(hdr, static_cast<std::uint32_t>(sections_.size()));
        detail::putU32(hdr, 0); // reserved
        for (std::size_t i = 0; i < sections_.size(); ++i) {
            const Section& s = sections_[i];
            detail::putU32(hdr, static_cast<std::uint32_t>(s.type));
            detail::putU16(hdr, s.version);
            detail::putU16(hdr, s.recordSize);
            detail::putU64(hdr, offsetOf(i));
            detail::putU64(hdr, s.data.size());
            detail::putU64(hdr, s.count);
        }
        sink(hdr.data(), hdr.size());
    }
};

/// A parsed view of one section (borrows the container's bytes).
struct SectionView {
    SectionType type;
    std::uint16_t version;
    std::uint16_t recordSize;
    std::uint64_t count;
    std::span<const std::byte> data;

    /// Reinterprets a fixed-width record section as a span of `T` (zero-copy). Empty if the
    /// stored record size doesn't match `sizeof(T)`.
    template <typename T>
    [[nodiscard]] std::span<const T> records() const {
        static_assert(std::is_trivially_copyable_v<T>, "record type must be trivially copyable");
        if (recordSize != sizeof(T) || data.size() % sizeof(T) != 0) {
            return {};
        }
        return {reinterpret_cast<const T*>(data.data()), data.size() / sizeof(T)};
    }
};

/// Parses a container from a contiguous byte buffer that the caller keeps alive (e.g. an mmap).
/// Read-only; validates the magic, endianness flag, and section-table bounds.
class ContainerReader {
public:
    explicit ContainerReader(std::span<const std::byte> bytes) : bytes_(bytes) { parse(); }

    [[nodiscard]] bool valid() const { return valid_; }
    [[nodiscard]] std::uint16_t version() const { return version_; }
    [[nodiscard]] const std::vector<SectionView>& sections() const { return sections_; }

    [[nodiscard]] std::optional<SectionView> find(SectionType type) const {
        for (const SectionView& s : sections_) {
            if (s.type == type) {
                return s;
            }
        }
        return std::nullopt;
    }

    /// All sections of `type`, in table (add) order — for a column split across chunks by
    /// addChunkedRecords. The real consumer is the C# reader; this exists for round-trip tests.
    [[nodiscard]] std::vector<SectionView> findAll(SectionType type) const {
        std::vector<SectionView> out;
        for (const SectionView& s : sections_) {
            if (s.type == type) {
                out.push_back(s);
            }
        }
        return out;
    }

private:
    void parse() {
        const auto* p = reinterpret_cast<const std::uint8_t*>(bytes_.data());
        const std::size_t n = bytes_.size();
        if (n < kHeaderSize || std::memcmp(p, kMagic, 4) != 0) {
            return;
        }
        version_ = detail::getU16(p + 4);
        const std::uint16_t flags = detail::getU16(p + 6);
        if ((flags & kFlagLittleEndian) == 0) {
            return; // only little-endian is supported
        }
        const std::uint32_t count = detail::getU32(p + 8);
        if (kHeaderSize + static_cast<std::size_t>(count) * kSectionEntrySize > n) {
            return;
        }
        for (std::uint32_t i = 0; i < count; ++i) {
            const std::uint8_t* e = p + kHeaderSize + static_cast<std::size_t>(i) * kSectionEntrySize;
            const std::uint64_t off = detail::getU64(e + 8);
            const std::uint64_t len = detail::getU64(e + 16);
            if (off > n || len > n || off + len > n) {
                sections_.clear();
                return; // out-of-bounds section
            }
            SectionView s;
            s.type = static_cast<SectionType>(detail::getU32(e));
            s.version = detail::getU16(e + 4);
            s.recordSize = detail::getU16(e + 6);
            s.count = detail::getU64(e + 24);
            s.data = bytes_.subspan(off, len);
            sections_.push_back(s);
        }
        valid_ = true;
    }

    std::span<const std::byte> bytes_;
    bool valid_ = false;
    std::uint16_t version_ = 0;
    std::vector<SectionView> sections_;
};

} // namespace Sherlock::storage
