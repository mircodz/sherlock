#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

// Wire framing + message helpers for the sl <-> profiler control channel.
//
// A message is a 4-byte little-endian length followed by a UTF-8 payload. The payload
// is tab-separated fields; the first field is the verb:
//   HELLO \t <version> \t <comma,separated,features>   profiler -> sl on connect
//   REQ   \t <id> \t <command> [\t args...]            sl -> profiler
//   RES   \t <id> \t ok|err    [\t detail]             profiler -> sl
//   EVENT \t <name> [\t args...]                       profiler -> sl (unsolicited)
//
// Everything here is pure (no CLR/OS deps) so it can be unit-tested directly.
namespace Sherlock::control {

/// Prepends the 4-byte little-endian length to a payload.
[[nodiscard]] inline std::string frame(std::string_view payload) {
    const auto len = static_cast<std::uint32_t>(payload.size());
    std::string out;
    out.reserve(4 + payload.size());
    out.push_back(static_cast<char>(len & 0xFF));
    out.push_back(static_cast<char>((len >> 8) & 0xFF));
    out.push_back(static_cast<char>((len >> 16) & 0xFF));
    out.push_back(static_cast<char>((len >> 24) & 0xFF));
    out.append(payload);
    return out;
}

/// Returns the payload of the next complete frame, consuming it from `buffer`. Returns
/// nullopt (leaving `buffer` intact) when it doesn't yet hold a full frame.
[[nodiscard]] inline std::optional<std::string> tryReadFrame(std::string& buffer) {
    if (buffer.size() < 4) {
        return std::nullopt;
    }
    const std::uint32_t len = static_cast<std::uint8_t>(buffer[0]) |
                              (static_cast<std::uint32_t>(static_cast<std::uint8_t>(buffer[1])) << 8) |
                              (static_cast<std::uint32_t>(static_cast<std::uint8_t>(buffer[2])) << 16) |
                              (static_cast<std::uint32_t>(static_cast<std::uint8_t>(buffer[3])) << 24);
    if (buffer.size() < std::size_t{4} + len) {
        return std::nullopt;
    }
    std::string payload = buffer.substr(4, len);
    buffer.erase(0, std::size_t{4} + len);
    return payload;
}

/// Splits a payload into tab-separated fields (views into `payload`, which must outlive
/// them). Always returns at least one field.
[[nodiscard]] inline std::vector<std::string_view> splitFields(std::string_view payload) {
    std::vector<std::string_view> fields;
    std::size_t start = 0;
    for (;;) {
        const std::size_t tab = payload.find('\t', start);
        if (tab == std::string_view::npos) {
            fields.push_back(payload.substr(start));
            break;
        }
        fields.push_back(payload.substr(start, tab - start));
        start = tab + 1;
    }
    return fields;
}

/// Joins fields with tabs — the inverse of splitFields.
template <typename Range>
[[nodiscard]] inline std::string joinFields(const Range& fields) {
    std::string out;
    bool first = true;
    for (std::string_view f : fields) {
        if (!first) {
            out += '\t';
        }
        out += f;
        first = false;
    }
    return out;
}

} // namespace Sherlock::control
