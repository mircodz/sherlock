#pragma once

#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include "profilercommon.h"

namespace Sherlock {

// Metadata names are UTF-16; profiles store UTF-8. A lone surrogate makes the text invalid: the result is
// nullopt, or U+FFFD in its place when replaceInvalid is set.
inline std::optional<std::string> utf8(std::span<const WCHAR> text, bool replaceInvalid = false) {
    std::string result;
    result.reserve(text.size());
    for (std::size_t i = 0; i < text.size(); ++i) {
        std::uint32_t value = text[i];
        if (value >= 0xd800 && value <= 0xdbff && i + 1 < text.size() && text[i + 1] >= 0xdc00 && text[i + 1] <= 0xdfff) {
            value = 0x10000 + ((value - 0xd800) << 10) + (text[++i] - 0xdc00);
        } else if (value >= 0xd800 && value <= 0xdfff) {
            if (!replaceInvalid) {
                return std::nullopt;
            }
            value = 0xfffd;
        }

        if (value <= 0x7f) {
            result.push_back(static_cast<char>(value));
        } else if (value <= 0x7ff) {
            result.push_back(static_cast<char>(0xc0 | (value >> 6)));
            result.push_back(static_cast<char>(0x80 | (value & 0x3f)));
        } else if (value <= 0xffff) {
            result.push_back(static_cast<char>(0xe0 | (value >> 12)));
            result.push_back(static_cast<char>(0x80 | ((value >> 6) & 0x3f)));
            result.push_back(static_cast<char>(0x80 | (value & 0x3f)));
        } else {
            result.push_back(static_cast<char>(0xf0 | (value >> 18)));
            result.push_back(static_cast<char>(0x80 | ((value >> 12) & 0x3f)));
            result.push_back(static_cast<char>(0x80 | ((value >> 6) & 0x3f)));
            result.push_back(static_cast<char>(0x80 | (value & 0x3f)));
        }
    }
    return result;
}

// Null-terminated UTF-16 for metadata lookups by ASCII name, whether WCHAR is char16_t or wchar_t.
inline std::vector<WCHAR> widen(std::string_view ascii) {
    std::vector<WCHAR> wide;
    wide.reserve(ascii.size() + 1);
    for (char c : ascii)
        wide.push_back(static_cast<WCHAR>(static_cast<unsigned char>(c)));
    wide.push_back(0);
    return wide;
}

} // namespace Sherlock
