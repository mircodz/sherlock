#include "sherlock/common/logger.hpp"

#include <iostream>

namespace Sherlock {

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
        case LogLevel::Info:    return "INFO ";
        case LogLevel::Warning: return "WARN ";
        case LogLevel::Error:   return "ERROR";
    }
    return "?????";
}

} // namespace Sherlock
