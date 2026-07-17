#pragma once

#include <atomic>
#include <mutex>
#include <string>

namespace Sherlock {

/// Minimal thread-safe logger. Writes to stderr at one of four severity levels;
/// messages below the configured level are dropped.
class Logger {
public:
    enum class LogLevel {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
    };

    Logger() = default;

    Logger(const Logger&) = delete;
    Logger& operator=(const Logger&) = delete;

    // min_level_ is atomic so setLogLevel (any thread) races cleanly with the level check on the
    // logging paths (called from many app threads); relaxed is fine — it only gates verbosity.
    void setLogLevel(LogLevel level) { min_level_.store(level, std::memory_order_relaxed); }
    LogLevel getLogLevel() const { return min_level_.load(std::memory_order_relaxed); }

    void logDebug(const std::string& message) { log(LogLevel::Debug, message); }
    void logInfo(const std::string& message) { log(LogLevel::Info, message); }
    void logWarning(const std::string& message) { log(LogLevel::Warning, message); }
    void logError(const std::string& message) { log(LogLevel::Error, message); }

private:
    void log(LogLevel level, const std::string& message);
    static const char* levelName(LogLevel level);

    std::atomic<LogLevel> min_level_{LogLevel::Info};
    std::mutex mutex_;
};

} // namespace Sherlock
