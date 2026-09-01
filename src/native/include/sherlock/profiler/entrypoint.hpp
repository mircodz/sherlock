#pragma once

#include <cstdint>
#include <filesystem>
#include <optional>
#include <span>

namespace Sherlock::entrypoint {

std::optional<std::uint32_t> fromImage(std::span<const std::uint8_t> image);
std::optional<std::uint32_t> fromFile(const std::filesystem::path& path);

} // namespace Sherlock::entrypoint
