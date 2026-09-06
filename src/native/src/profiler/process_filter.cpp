#include "sherlock/profiler/process_filter.hpp"

#include <array>
#include <cstdint>
#include <exception>
#include <fstream>
#include <utility>
#include <vector>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <shellapi.h>
#elif defined(__APPLE__)
#include <crt_externs.h>
#include <mach-o/dyld.h>
#elif defined(__linux__)
#include <unistd.h>
#endif

namespace Sherlock::process_filter {
namespace {

constexpr std::size_t kMaxCommandLineBytes = 1024 * 1024;

unsigned char fold(unsigned char value) {
    return value >= 'A' && value <= 'Z' ? value + ('a' - 'A') : value;
}

bool equal(std::string_view left, std::string_view right) {
    if (left.size() != right.size()) {
        return false;
    }
    for (std::size_t i = 0; i < left.size(); ++i) {
        if (fold(left[i]) != fold(right[i])) {
            return false;
        }
    }
    return true;
}

bool endsWith(std::string_view value, std::string_view suffix) {
    return value.size() >= suffix.size() && equal(value.substr(value.size() - suffix.size()), suffix);
}

std::string_view basename(std::string_view path) {
    const auto separator = path.find_last_of("/\\");
    return separator == std::string_view::npos ? path : path.substr(separator + 1);
}

std::size_t nextCharacter(std::string_view value, std::size_t offset) {
    const auto first = static_cast<unsigned char>(value[offset]);
    std::size_t length = 1;
    if (first >= 0xc2 && first <= 0xdf) {
        length = 2;
    } else if (first >= 0xe0 && first <= 0xef) {
        length = 3;
    } else if (first >= 0xf0 && first <= 0xf4) {
        length = 4;
    }
    if (length > value.size() - offset) {
        return offset + 1;
    }
    for (std::size_t i = 1; i < length; ++i) {
        const auto byte = static_cast<unsigned char>(value[offset + i]);
        if (byte < 0x80 || byte > 0xbf) {
            return offset + 1;
        }
    }
    if (length > 1) {
        const auto second = static_cast<unsigned char>(value[offset + 1]);
        if ((first == 0xe0 && second < 0xa0) || (first == 0xed && second >= 0xa0)
            || (first == 0xf0 && second < 0x90) || (first == 0xf4 && second >= 0x90)) {
            return offset + 1;
        }
    }
    // Invalid UTF-8 is matched bytewise; valid ? and * steps never split a code point.
    return offset + length;
}

bool matches(std::string_view value, std::string_view pattern) {
    std::size_t text = 0;
    std::size_t glob = 0;
    std::size_t star = std::string_view::npos;
    std::size_t retry = 0;
    while (text < value.size()) {
        if (glob < pattern.size() && pattern[glob] == '*') {
            star = ++glob;
            retry = text;
        } else if (glob < pattern.size() && pattern[glob] == '?') {
            text = nextCharacter(value, text);
            ++glob;
        } else if (glob < pattern.size() && fold(value[text]) == fold(pattern[glob])) {
            ++text;
            ++glob;
        } else if (star != std::string_view::npos && retry < value.size()) {
            retry = nextCharacter(value, retry);
            text = retry;
            glob = star;
        } else {
            return false;
        }
    }
    while (glob < pattern.size() && pattern[glob] == '*') {
        ++glob;
    }
    return glob == pattern.size();
}

bool hostOption(std::string_view option) {
    constexpr std::array options{
        "--runtimeconfig", "--depsfile", "--additionalprobingpath", "--additional-deps",
        "--roll-forward", "--fx-version", "--roll-forward-on-no-candidate-fx"
    };
    for (const auto known : options) {
        if (equal(option, known)) {
            return true;
        }
    }
    return false;
}

#if defined(_WIN32) || defined(__APPLE__)
template <typename Character>
std::size_t boundedLength(const Character* value, std::size_t limit) {
    std::size_t length = 0;
    while (length < limit && value[length] != 0) {
        ++length;
    }
    return length;
}
#endif

#if defined(_WIN32)
bool utf8(const wchar_t* value, std::size_t length, std::string& result) {
    if (length == 0) {
        result.clear();
        return true;
    }
    const int bytes = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, static_cast<int>(length), nullptr, 0, nullptr, nullptr);
    if (bytes <= 0 || static_cast<std::size_t>(bytes) > kMaxCommandLineBytes) {
        return false;
    }
    result.resize(bytes);
    return WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, static_cast<int>(length), result.data(), bytes, nullptr, nullptr) == bytes;
}
#endif

Selection readCurrent(std::string_view patterns) {
    std::vector<std::string> arguments;
    std::string executable;
#if defined(_WIN32)
    const auto command = GetCommandLineW();
    constexpr auto maxUnits = kMaxCommandLineBytes / sizeof(wchar_t);
    if (command == nullptr || boundedLength(command, maxUnits) == maxUnits) {
        return {false, {}, "Cannot read process command line: missing or exceeds 1 MiB"};
    }
    int count = 0;
    struct LocalArguments {
        wchar_t** values;
        ~LocalArguments() { LocalFree(values); }
    } parsed{CommandLineToArgvW(command, &count)};
    if (parsed.values == nullptr || count <= 0 || static_cast<std::size_t>(count) > kMaxCommandLineBytes) {
        return {false, {}, "Cannot parse process command line"};
    }
    std::size_t bytes = 0;
    for (int i = 0; i < count; ++i) {
        const auto length = boundedLength(parsed.values[i], maxUnits);
        std::string argument;
        if (length == maxUnits || !utf8(parsed.values[i], length, argument)) {
            return {false, {}, "Cannot convert process argument to UTF-8"};
        }
        if (argument.size() + 1 > kMaxCommandLineBytes - bytes) {
            return {false, {}, "Process command line exceeds 1 MiB"};
        }
        bytes += argument.size() + 1;
        arguments.push_back(std::move(argument));
    }
    std::vector<wchar_t> path(maxUnits);
    const auto length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (length == 0 || length >= path.size() || !utf8(path.data(), length, executable)) {
        return {false, {}, "Cannot read process executable path"};
    }
#elif defined(__APPLE__)
    const auto count = _NSGetArgc();
    const auto argv = _NSGetArgv();
    if (count == nullptr || *count <= 0 || static_cast<std::size_t>(*count) > kMaxCommandLineBytes || argv == nullptr || *argv == nullptr) {
        return {false, {}, "Cannot read process arguments"};
    }
    std::size_t bytes = 0;
    for (int i = 0; i < *count; ++i) {
        if ((*argv)[i] == nullptr) {
            return {false, {}, "Cannot read process argument"};
        }
        const auto remaining = kMaxCommandLineBytes - bytes;
        const auto length = boundedLength((*argv)[i], remaining);
        if (length == remaining) {
            return {false, {}, "Process command line exceeds 1 MiB"};
        }
        bytes += length + 1;
        arguments.emplace_back((*argv)[i], length);
    }
    std::uint32_t size = 0;
    _NSGetExecutablePath(nullptr, &size);
    if (size == 0 || size > kMaxCommandLineBytes) {
        return {false, {}, "Cannot read process executable path: missing or exceeds 1 MiB"};
    }
    std::vector<char> path(size);
    if (_NSGetExecutablePath(path.data(), &size) != 0) {
        return {false, {}, "Cannot read process executable path"};
    }
    const auto length = boundedLength(path.data(), path.size());
    if (length == path.size()) {
        return {false, {}, "Process executable path is not terminated"};
    }
    executable.assign(path.data(), length);
#elif defined(__linux__)
    std::ifstream command("/proc/self/cmdline", std::ios::binary);
    if (!command) {
        return {false, {}, "Cannot open /proc/self/cmdline"};
    }
    std::string data(kMaxCommandLineBytes + 1, '\0');
    command.read(data.data(), static_cast<std::streamsize>(data.size()));
    if (command.bad() || (!command.eof() && command.fail())) {
        return {false, {}, "Cannot read /proc/self/cmdline"};
    }
    data.resize(static_cast<std::size_t>(command.gcount()));
    if (data.size() > kMaxCommandLineBytes) {
        return {false, {}, "Process command line exceeds 1 MiB"};
    }
    if (data.empty() || data.back() != '\0') {
        return {false, {}, "Process command line is empty or not terminated"};
    }
    for (std::size_t offset = 0; offset < data.size();) {
        const auto end = data.find('\0', offset);
        arguments.emplace_back(data.data() + offset, end - offset);
        offset = end + 1;
    }
    std::vector<char> path(kMaxCommandLineBytes);
    const auto length = readlink("/proc/self/exe", path.data(), path.size());
    if (length <= 0 || static_cast<std::size_t>(length) >= path.size()) {
        return {false, {}, "Cannot read /proc/self/exe"};
    }
    executable.assign(path.data(), static_cast<std::size_t>(length));
#else
    return {false, {}, "Process selection is unsupported on this platform"};
#endif
    return select(arguments, executable, patterns);
}

} // namespace

Selection current(std::string_view patterns) {
    try {
        return readCurrent(patterns);
    } catch (const std::exception& error) {
        return {false, {}, std::string("Cannot select current process: ") + error.what()};
    } catch (...) {
        return {false, {}, "Cannot select current process: unknown failure"};
    }
}

Selection select(std::span<const std::string> arguments, std::string_view executable, std::string_view patterns) {
    std::vector<std::string_view> allowlist;
    for (std::size_t offset = 0;;) {
        const auto end = patterns.find('\n', offset);
        const auto pattern = patterns.substr(offset, end == std::string_view::npos ? end : end - offset);
        if (pattern.empty() || pattern.find_first_not_of(' ') == std::string_view::npos) {
            return {false, {}, "Process patterns must not be blank"};
        }
        for (const unsigned char byte : pattern) {
            if (byte == '/' || byte == '\\') {
                return {false, {}, "Process patterns must be filenames, not paths"};
            }
            if (byte < 0x20 || byte == 0x7f) {
                return {false, {}, "Process patterns must not contain ASCII controls"};
            }
        }
        allowlist.push_back(pattern);
        if (end == std::string_view::npos) {
            break;
        }
        offset = end + 1;
    }

    if (executable.empty() && !arguments.empty()) {
        executable = arguments.front();
    }
    std::string name(basename(executable));
    if (name.empty()) {
        return {false, {}, "Cannot determine process executable name"};
    }
    const bool dotnet = equal(name, "dotnet") || equal(name, "dotnet.exe");
    if (dotnet) {
        std::size_t index = 1;
        if (index < arguments.size() && equal(arguments[index], "exec")) {
            ++index;
        }
        while (index < arguments.size()) {
            const std::string_view argument = arguments[index];
            const auto equals = argument.find('=');
            if (!hostOption(argument.substr(0, equals))) {
                break;
            }
            if (equals != std::string_view::npos) {
                if (equals + 1 == argument.size()) {
                    return {false, name, "Missing value for dotnet host option"};
                }
                ++index;
            } else {
                if (index + 1 >= arguments.size() || arguments[index + 1].empty()) {
                    return {false, name, "Missing value for dotnet host option"};
                }
                index += 2;
            }
        }
        // Only the first positional argument can identify the entry. Later DLLs are application/SDK inputs.
        if (index < arguments.size() && !arguments[index].starts_with('-')) {
            const auto entry = basename(arguments[index]);
            if (endsWith(entry, ".dll") || endsWith(entry, ".exe")) {
                name = entry;
            }
        }
    }
    for (const auto pattern : allowlist) {
        if (matches(name, pattern)) {
            return {true, name, {}};
        }
    }
    if (!dotnet && !endsWith(name, ".dll")) {
        const auto stem = endsWith(name, ".exe") ? name.substr(0, name.size() - 4) : name;
        const auto alias = stem + ".dll";
        for (const auto pattern : allowlist) {
            if (matches(alias, pattern)) {
                return {true, alias, {}};
            }
        }
    }
    return {false, name, {}};
}

} // namespace Sherlock::process_filter
