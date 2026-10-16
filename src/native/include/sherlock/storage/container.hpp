#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>
#include <optional>
#include <ostream>
#include <span>
#include <string>
#include <type_traits>
#include <vector>

// A header, a section table, and page-aligned sections, matching Sherlock.Core.Storage:
//
//   0   "SHRK"
//   4   u16 container version
//   6   u16 kind (what the file holds)
//   8   u32 kind version
//   12  u32 section count
//   16  section table, 24 bytes each: u32 type, u32 record size, u64 offset, u64 length
//       sections, each starting on a kAlignment boundary
//
// Everything is little-endian. Record payloads are written in their in-memory layout.
namespace Sherlock::storage {

inline constexpr char kMagic[4] = {'S', 'H', 'R', 'K'};
inline constexpr std::uint16_t kContainerVersion = 2;
inline constexpr std::size_t kHeaderSize = 16;
inline constexpr std::size_t kSectionEntrySize = 24;
inline constexpr std::size_t kAlignment = 4096;

// Keep values in sync with the C# SlabKind enum.
enum class SlabKind : std::uint16_t {
    Profile = 1,
};

// Keep values in sync with the C# SectionType enum; the native profiler writes only profile sections.
enum class SectionType : std::uint32_t {
    Strings = 1,
    Modules = 2,
    Methods = 3,
    Types = 4,
    Stacks = 5,
    StackFrames = 6,
    Allocations = 7,
    ObjectAddresses = 8,
    ObjectStacks = 9,
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

// Emits sections in insertion order, without trailing padding. Sections borrow their data until the container
// is written, unless the writer is given ownership of it.
class ContainerWriter {
public:
    ContainerWriter(SlabKind kind, std::uint32_t kindVersion) : kind_(kind), kindVersion_(kindVersion) {}

    void addBytes(SectionType type, std::span<const std::byte> data) { add(type, 1, data); }

    template <typename T>
    void addRecords(SectionType type, std::span<const T> records) {
        static_assert(std::is_trivially_copyable_v<T>, "record type must be trivially copyable");
        add(type, static_cast<std::uint32_t>(sizeof(T)), std::as_bytes(records));
    }

    // Takes the records, for sections built only to be written.
    template <typename T>
    void addRecords(SectionType type, std::vector<T>&& records) {
        auto owned = std::make_shared<const std::vector<T>>(std::move(records));
        addRecords<T>(type, std::span<const T>(*owned));
        owned_.push_back(std::move(owned));
    }

    // Materializes the whole file; use writeTo for large containers.
    [[nodiscard]] std::string finish() const {
        std::string out = headerAndTable();
        for (std::size_t i = 0; i < sections_.size(); ++i) {
            out.resize(offsetOf(i), '\0');
            out.append(reinterpret_cast<const char*>(sections_[i].data.data()), sections_[i].data.size());
        }
        return out;
    }

    // Byte-identical to finish(), without another full-file buffer. Returns stream success.
    bool writeTo(std::ostream& os) const {
        const std::string head = headerAndTable();
        os.write(head.data(), static_cast<std::streamsize>(head.size()));
        std::size_t pos = head.size();
        static constexpr char kZeros[256] = {};
        for (std::size_t i = 0; i < sections_.size(); ++i) {
            for (std::size_t gap = offsetOf(i) - pos; gap > 0;) {
                const std::size_t n = gap < sizeof(kZeros) ? gap : sizeof(kZeros);
                os.write(kZeros, static_cast<std::streamsize>(n));
                gap -= n;
            }
            os.write(reinterpret_cast<const char*>(sections_[i].data.data()), static_cast<std::streamsize>(sections_[i].data.size()));
            pos = offsetOf(i) + sections_[i].data.size();
        }
        return static_cast<bool>(os);
    }

private:
    struct Section {
        SectionType type;
        std::uint32_t recordSize;
        std::span<const std::byte> data;
    };

    void add(SectionType type, std::uint32_t recordSize, std::span<const std::byte> data) {
        sections_.push_back({type, recordSize, data});
    }

    std::size_t offsetOf(std::size_t i) const {
        std::size_t cursor = detail::alignUp(kHeaderSize + kSectionEntrySize * sections_.size(), kAlignment);
        for (std::size_t k = 0; k < i; ++k) {
            cursor = detail::alignUp(cursor + sections_[k].data.size(), kAlignment);
        }
        return cursor;
    }

    std::string headerAndTable() const {
        std::string head;
        head.append(kMagic, 4);
        detail::putU16(head, kContainerVersion);
        detail::putU16(head, static_cast<std::uint16_t>(kind_));
        detail::putU32(head, kindVersion_);
        detail::putU32(head, static_cast<std::uint32_t>(sections_.size()));
        for (std::size_t i = 0; i < sections_.size(); ++i) {
            detail::putU32(head, static_cast<std::uint32_t>(sections_[i].type));
            detail::putU32(head, sections_[i].recordSize);
            detail::putU64(head, offsetOf(i));
            detail::putU64(head, sections_[i].data.size());
        }
        return head;
    }

    SlabKind kind_;
    std::uint32_t kindVersion_;
    std::vector<Section> sections_;
    std::vector<std::shared_ptr<const void>> owned_;
};

// Borrows the container's bytes.
struct SectionView {
    SectionType type;
    std::uint32_t recordSize;
    std::span<const std::byte> data;

    // Zero-copy; empty if the record width doesn't match.
    template <typename T>
    [[nodiscard]] std::span<const T> records() const {
        static_assert(std::is_trivially_copyable_v<T>, "record type must be trivially copyable");
        if (recordSize != sizeof(T)) {
            return {};
        }
        return {reinterpret_cast<const T*>(data.data()), data.size() / sizeof(T)};
    }
};

// Reads containers back in tests; Sherlock reads them in C#. The caller keeps the buffer alive.
class ContainerReader {
public:
    explicit ContainerReader(std::span<const std::byte> bytes) : bytes_(bytes) { parse(); }

    [[nodiscard]] bool valid() const { return valid_; }
    [[nodiscard]] SlabKind kind() const { return kind_; }
    [[nodiscard]] std::uint32_t kindVersion() const { return kindVersion_; }
    [[nodiscard]] const std::vector<SectionView>& sections() const { return sections_; }

    [[nodiscard]] std::optional<SectionView> find(SectionType type) const {
        for (const SectionView& s : sections_) {
            if (s.type == type) {
                return s;
            }
        }
        return std::nullopt;
    }

private:
    void parse() {
        const auto* p = reinterpret_cast<const std::uint8_t*>(bytes_.data());
        const std::size_t n = bytes_.size();
        if (n < kHeaderSize || std::memcmp(p, kMagic, 4) != 0 || detail::getU16(p + 4) != kContainerVersion) {
            return;
        }
        kind_ = static_cast<SlabKind>(detail::getU16(p + 6));
        kindVersion_ = detail::getU32(p + 8);
        const std::uint32_t count = detail::getU32(p + 12);
        if (kHeaderSize + static_cast<std::size_t>(count) * kSectionEntrySize > n) {
            return;
        }
        for (std::uint32_t i = 0; i < count; ++i) {
            const std::uint8_t* e = p + kHeaderSize + static_cast<std::size_t>(i) * kSectionEntrySize;
            const std::uint32_t recordSize = detail::getU32(e + 4);
            const std::uint64_t off = detail::getU64(e + 8);
            const std::uint64_t len = detail::getU64(e + 16);
            if (recordSize == 0 || len % recordSize != 0 || off > n || len > n - off || (len > 0 && off % kAlignment != 0)) {
                sections_.clear();
                return;
            }
            sections_.push_back({static_cast<SectionType>(detail::getU32(e)), recordSize, bytes_.subspan(off, len)});
        }
        valid_ = true;
    }

    std::span<const std::byte> bytes_;
    bool valid_ = false;
    SlabKind kind_{};
    std::uint32_t kindVersion_ = 0;
    std::vector<SectionView> sections_;
};

} // namespace Sherlock::storage
