#pragma once

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

    void setLogLevel(LogLevel level) { min_level_ = level; }
    LogLevel getLogLevel() const { return min_level_; }

    void logDebug(const std::string& message) { log(LogLevel::Debug, message); }
    void logInfo(const std::string& message) { log(LogLevel::Info, message); }
    void logWarning(const std::string& message) { log(LogLevel::Warning, message); }
    void logError(const std::string& message) { log(LogLevel::Error, message); }

private:
    void log(LogLevel level, const std::string& message);
    static const char* levelName(LogLevel level);

    LogLevel min_level_ = LogLevel::Info;
    std::mutex mutex_;
};

} // namespace Sherlock
