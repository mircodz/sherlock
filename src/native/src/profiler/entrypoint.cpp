#include "sherlock/profiler/entrypoint.hpp"

#include <algorithm>
#include <fstream>
#include <vector>

namespace Sherlock::entrypoint {

namespace {

constexpr std::uint16_t kPe32 = 0x10b;
constexpr std::uint16_t kPe32Plus = 0x20b;
constexpr std::uint32_t kNativeEntryPoint = 0x10;
constexpr std::uint32_t kMethodDef = 0x06000000;
constexpr std::size_t kSectionSize = 40;
constexpr std::size_t kMaxImageSize = 512 * 1024 * 1024;

bool contains(std::span<const std::uint8_t> image, std::size_t offset, std::size_t size) {
    return offset <= image.size() && size <= image.size() - offset;
}

std::optional<std::uint16_t> read16(
    std::span<const std::uint8_t> image,
    std::size_t offset) {
    if (!contains(image, offset, 2)) {
        return std::nullopt;
    }
    return static_cast<std::uint16_t>(image[offset]) |
           (static_cast<std::uint16_t>(image[offset + 1]) << 8);
}

std::optional<std::uint32_t> read32(
    std::span<const std::uint8_t> image,
    std::size_t offset) {
    if (!contains(image, offset, 4)) {
        return std::nullopt;
    }
    return static_cast<std::uint32_t>(image[offset]) |
           (static_cast<std::uint32_t>(image[offset + 1]) << 8) |
           (static_cast<std::uint32_t>(image[offset + 2]) << 16) |
           (static_cast<std::uint32_t>(image[offset + 3]) << 24);
}

std::optional<std::size_t> fileOffset(
    std::span<const std::uint8_t> image,
    std::uint32_t rva,
    std::size_t size,
    std::size_t sectionTable,
    std::uint16_t sectionCount,
    std::uint32_t headerSize) {
    if (rva < headerSize && contains(image, rva, size)) {
        return rva;
    }

    for (std::uint16_t i = 0; i < sectionCount; ++i) {
        const std::size_t section = sectionTable + static_cast<std::size_t>(i) * kSectionSize;
        const auto virtualSize = read32(image, section + 8);
        const auto virtualAddress = read32(image, section + 12);
        const auto rawSize = read32(image, section + 16);
        const auto rawOffset = read32(image, section + 20);
        if (!virtualSize || !virtualAddress || !rawSize || !rawOffset) {
            return std::nullopt;
        }

        const std::uint64_t span = std::max(*virtualSize, *rawSize);
        if (rva < *virtualAddress ||
            static_cast<std::uint64_t>(rva) >=
                static_cast<std::uint64_t>(*virtualAddress) + span) {
            continue;
        }

        const std::uint64_t delta =
            static_cast<std::uint64_t>(rva) - *virtualAddress;
        if (delta + size > *rawSize) {
            return std::nullopt;
        }
        const std::uint64_t offset = static_cast<std::uint64_t>(*rawOffset) + delta;
        if (offset > image.size() || size > image.size() - offset) {
            return std::nullopt;
        }
        return static_cast<std::size_t>(offset);
    }
    return std::nullopt;
}

} // namespace

std::optional<std::uint32_t> fromImage(std::span<const std::uint8_t> image) {
    if (!contains(image, 0, 0x40) || image[0] != 'M' || image[1] != 'Z') {
        return std::nullopt;
    }

    const auto peOffset32 = read32(image, 0x3c);
    if (!peOffset32) {
        return std::nullopt;
    }
    const std::size_t pe = *peOffset32;
    if (!contains(image, pe, 24) ||
        image[pe] != 'P' || image[pe + 1] != 'E' ||
        image[pe + 2] != 0 || image[pe + 3] != 0) {
        return std::nullopt;
    }

    const auto sectionCount = read16(image, pe + 6);
    const auto optionalSize = read16(image, pe + 20);
    if (!sectionCount || !optionalSize) {
        return std::nullopt;
    }

    const std::size_t optional = pe + 24;
    if (!contains(image, optional, *optionalSize)) {
        return std::nullopt;
    }
    const auto magic = read16(image, optional);
    if (!magic || (*magic != kPe32 && *magic != kPe32Plus)) {
        return std::nullopt;
    }

    const std::size_t directory = optional + (*magic == kPe32 ? 96 : 112);
    const std::size_t directoryCountOffset = optional + (*magic == kPe32 ? 92 : 108);
    const auto directoryCount = read32(image, directoryCountOffset);
    if (!directoryCount || *directoryCount <= 14 ||
        directory + 15 * 8 > optional + *optionalSize) {
        return std::nullopt;
    }

    const auto corRva = read32(image, directory + 14 * 8);
    const auto corSize = read32(image, directory + 14 * 8 + 4);
    const auto headerSize = read32(image, optional + 60);
    if (!corRva || !corSize || !headerSize || *corRva == 0 || *corSize < 24) {
        return std::nullopt;
    }

    const std::size_t sectionTable = optional + *optionalSize;
    if (!contains(
            image,
            sectionTable,
            static_cast<std::size_t>(*sectionCount) * kSectionSize)) {
        return std::nullopt;
    }
    const auto cor = fileOffset(
        image, *corRva, 24, sectionTable, *sectionCount, *headerSize);
    if (!cor) {
        return std::nullopt;
    }

    const auto declaredSize = read32(image, *cor);
    const auto flags = read32(image, *cor + 16);
    const auto token = read32(image, *cor + 20);
    if (!declaredSize || !flags || !token || *declaredSize < 24 ||
        (*flags & kNativeEntryPoint) != 0 ||
        (*token & 0xff000000) != kMethodDef ||
        (*token & 0x00ffffff) == 0) {
        return std::nullopt;
    }
    return token;
}

std::optional<std::uint32_t> fromFile(const std::filesystem::path& path) {
    std::ifstream input(path, std::ios::binary | std::ios::ate);
    if (!input) {
        return std::nullopt;
    }
    const std::streamoff length = input.tellg();
    if (length <= 0 || static_cast<std::uint64_t>(length) > kMaxImageSize) {
        return std::nullopt;
    }

    std::vector<std::uint8_t> image(static_cast<std::size_t>(length));
    input.seekg(0);
    input.read(
        reinterpret_cast<char*>(image.data()),
        static_cast<std::streamsize>(image.size()));
    if (!input) {
        return std::nullopt;
    }
    return fromImage(image);
}

} // namespace Sherlock::entrypoint
