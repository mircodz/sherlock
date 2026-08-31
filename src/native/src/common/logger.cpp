#include "sherlock/common/logger.hpp"

#include <cstdlib>
#include <iostream>

namespace Sherlock {

Logger::Logger() noexcept {
    const char* value = std::getenv("SHERLOCK_LOG_LEVEL");
    if (value == nullptr) {
        return;
    }

    const std::string_view level(value);
    if (level == "trace") setLogLevel(LogLevel::Trace);
    else if (level == "info") setLogLevel(LogLevel::Info);
    else if (level == "warn" || level == "warning") setLogLevel(LogLevel::Warning);
    else if (level == "error") setLogLevel(LogLevel::Error);
    else if (level == "off" || level == "none") setLogLevel(LogLevel::Off);
}

void Logger::write(LogLevel level, std::string_view message) noexcept {
    if (!enabled(level)) {
        return;
    }
    writeEnabled(level, message);
}

void Logger::writeEnabled(LogLevel level, std::string_view message) noexcept {
    try {
        std::lock_guard lock(mutex_);
        std::cerr << "[sherlock] [" << levelName(level) << "] " << message << '\n';
    } catch (...) {
    }
}

std::string_view Logger::levelName(LogLevel level) noexcept {
    switch (level) {
        case LogLevel::Trace:   return "TRACE";
        case LogLevel::Info:    return "INFO ";
        case LogLevel::Warning: return "WARN ";
        case LogLevel::Error:   return "ERROR";
        case LogLevel::Off:     return "OFF  ";
    }
    return "?????";
}

} // namespace Sherlock
