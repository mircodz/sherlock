#include "sherlock/common/logger.hpp"

#include <iostream>

namespace Sherlock {

void Logger::log(LogLevel level, const std::string& message) {
    if (level < min_level_) {
        return;
    }

    std::lock_guard<std::mutex> lock(mutex_);
    std::cerr << "[sherlock] [" << levelName(level) << "] " << message << '\n';
}

const char* Logger::levelName(LogLevel level) {
    switch (level) {
        case LogLevel::Debug:   return "DEBUG";
        case LogLevel::Info:    return "INFO ";
        case LogLevel::Warning: return "WARN ";
        case LogLevel::Error:   return "ERROR";
    }
    return "?????";
}

} // namespace Sherlock
