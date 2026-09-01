#include "sherlock/profiler/entrypoint.hpp"

#include <gtest/gtest.h>

#include <cstdint>
#include <vector>

using namespace Sherlock;

namespace {

void put16(std::vector<std::uint8_t>& image, std::size_t offset, std::uint16_t value) {
    image[offset] = static_cast<std::uint8_t>(value);
    image[offset + 1] = static_cast<std::uint8_t>(value >> 8);
}

void put32(std::vector<std::uint8_t>& image, std::size_t offset, std::uint32_t value) {
    image[offset] = static_cast<std::uint8_t>(value);
    image[offset + 1] = static_cast<std::uint8_t>(value >> 8);
    image[offset + 2] = static_cast<std::uint8_t>(value >> 16);
    image[offset + 3] = static_cast<std::uint8_t>(value >> 24);
}

std::vector<std::uint8_t> managedImage(bool pe32Plus = true) {
    std::vector<std::uint8_t> image(0x400);
    image[0] = 'M';
    image[1] = 'Z';
    put32(image, 0x3c, 0x80);

    const std::size_t pe = 0x80;
    image[pe] = 'P';
    image[pe + 1] = 'E';
    put16(image, pe + 6, 1);
    put16(image, pe + 20, pe32Plus ? 0xf0 : 0xe0);

    const std::size_t optional = pe + 24;
    put16(image, optional, pe32Plus ? 0x20b : 0x10b);
    put32(image, optional + 60, 0x200);
    const std::size_t directories = optional + (pe32Plus ? 112 : 96);
    put32(image, optional + (pe32Plus ? 108 : 92), 16);
    put32(image, directories + 14 * 8, 0x2000);
    put32(image, directories + 14 * 8 + 4, 72);

    const std::size_t section = optional + (pe32Plus ? 0xf0 : 0xe0);
    put32(image, section + 8, 0x200);
    put32(image, section + 12, 0x2000);
    put32(image, section + 16, 0x200);
    put32(image, section + 20, 0x200);

    put32(image, 0x200, 72);
    put32(image, 0x210, 1);
    put32(image, 0x214, 0x0600002a);
    return image;
}

} // namespace

TEST(EntryPoint, ReadsPe32PlusManagedMethod) {
    EXPECT_EQ(entrypoint::fromImage(managedImage()), 0x0600002au);
}

TEST(EntryPoint, ReadsPe32ManagedMethod) {
    EXPECT_EQ(entrypoint::fromImage(managedImage(false)), 0x0600002au);
}

TEST(EntryPoint, RejectsLibraryAndNativeEntryPoints) {
    std::vector<std::uint8_t> library = managedImage();
    put32(library, 0x214, 0);
    EXPECT_FALSE(entrypoint::fromImage(library).has_value());

    std::vector<std::uint8_t> native = managedImage();
    put32(native, 0x210, 0x10);
    EXPECT_FALSE(entrypoint::fromImage(native).has_value());
}

TEST(EntryPoint, RejectsTruncatedAndOutOfRangeImages) {
    EXPECT_FALSE(entrypoint::fromImage(std::vector<std::uint8_t>(16)).has_value());

    std::vector<std::uint8_t> invalid = managedImage();
    put32(invalid, 0x80 + 24 + 112 + 14 * 8, 0xfffffff0);
    EXPECT_FALSE(entrypoint::fromImage(invalid).has_value());
}
