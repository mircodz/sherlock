#include "sherlock/profiler/triggers.hpp"

#include <cstdlib>

namespace Sherlock {

void SnapshotTriggers::add(Kind kind, std::string arg, std::string display) {
    int minGen = 0;
    if (kind == Kind::Gc && !arg.empty()) {
        const char* p = arg.c_str();
        if (arg.rfind("gen", 0) == 0) p += 3; // accept "gen2" or "2"
        minGen = std::atoi(p);
    }

    triggers_.emplace_back();
    Trigger& t = triggers_.back();
    t.kind = kind;
    t.arg = std::move(arg);
    t.minGen = minGen;
    t.display = std::move(display);

    switch (kind) {
        case Kind::Alloc: ++alloc_; break;
        case Kind::Gc:    ++gc_;    break;
        case Kind::Throw: ++throw_; break;
    }
}

std::optional<std::string> SnapshotTriggers::matchType(Kind kind, std::string_view typeName) {
    for (Trigger& t : triggers_) {
        if (t.kind != kind) {
            continue;
        }
        // Throw with an empty arg matches any exception; otherwise exact type match.
        const bool matches = (t.arg.empty() && kind == Kind::Throw) || t.arg == typeName;
        if (matches && !t.fired.exchange(true)) {
            return t.display;
        }
    }
    return std::nullopt;
}

std::optional<std::string> SnapshotTriggers::onAlloc(std::string_view typeName) {
    return matchType(Kind::Alloc, typeName);
}

std::optional<std::string> SnapshotTriggers::onThrow(std::string_view typeName) {
    return matchType(Kind::Throw, typeName);
}

std::optional<std::string> SnapshotTriggers::onGc(int generation) {
    for (Trigger& t : triggers_) {
        if (t.kind == Kind::Gc && generation >= t.minGen && !t.fired.exchange(true)) {
            return t.display;
        }
    }
    return std::nullopt;
}

} // namespace Sherlock
