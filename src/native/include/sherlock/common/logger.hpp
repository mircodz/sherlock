#pragma once

#include <atomic>
#include <format>
#include <mutex>
#include <string>
#include <string_view>
#include <utility>

namespace Sherlock {

class Logger {
public:
    enum class LogLevel {
        Trace = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        Off = 4,
    };

    Logger() noexcept;

    Logger(const Logger&) = delete;
    Logger& operator=(const Logger&) = delete;

    void setLogLevel(LogLevel level) { min_level_.store(level, std::memory_order_relaxed); }
    LogLevel getLogLevel() const { return min_level_.load(std::memory_order_relaxed); }

    void trace(std::string_view message) noexcept { write(LogLevel::Trace, message); }
    void info(std::string_view message) noexcept { write(LogLevel::Info, message); }
    void warn(std::string_view message) noexcept { write(LogLevel::Warning, message); }
    void error(std::string_view message) noexcept { write(LogLevel::Error, message); }

    template <typename... Args>
    void trace(std::format_string<Args...> format, Args&&... args) noexcept {
        writeFormatted(LogLevel::Trace, format, std::forward<Args>(args)...);
    }

    template <typename... Args>
    void info(std::format_string<Args...> format, Args&&... args) noexcept {
        writeFormatted(LogLevel::Info, format, std::forward<Args>(args)...);
    }

    template <typename... Args>
    void warn(std::format_string<Args...> format, Args&&... args) noexcept {
        writeFormatted(LogLevel::Warning, format, std::forward<Args>(args)...);
    }

    template <typename... Args>
    void error(std::format_string<Args...> format, Args&&... args) noexcept {
        writeFormatted(LogLevel::Error, format, std::forward<Args>(args)...);
    }

private:
    bool enabled(LogLevel level) const noexcept {
        return level >= min_level_.load(std::memory_order_relaxed);
    }

    template <typename... Args>
    void writeFormatted(
        LogLevel level, std::format_string<Args...> format, Args&&... args) noexcept {
        if (!enabled(level)) {
            return;
        }

        try {
            writeEnabled(level, std::format(format, std::forward<Args>(args)...));
        } catch (...) {
        }
    }

    void write(LogLevel level, std::string_view message) noexcept;
    void writeEnabled(LogLevel level, std::string_view message) noexcept;
    static std::string_view levelName(LogLevel level) noexcept;

    std::atomic<LogLevel> min_level_{LogLevel::Warning};
    std::mutex mutex_;
};

} // namespace Sherlock
