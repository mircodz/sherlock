#pragma once

#include <atomic>
#include <deque>
#include <optional>
#include <string>
#include <string_view>

namespace Sherlock {

// One-shot alloc/gc/throw triggers run in CLR callbacks; sl captures the heap out of process.
// ProbeManager handles call triggers through ReJIT.
class SnapshotTriggers {
public:
    enum class Kind { Alloc, Gc, Throw };

    // arg is a type name or minimum GC generation ("gen2"; empty = any).
    // display is the label sent to sl.
    void add(Kind kind, std::string arg, std::string display);

    bool wantsAlloc() const { return alloc_ > 0; }
    bool wantsGc() const { return gc_ > 0; }
    bool wantsThrow() const { return throw_ > 0; }

    // Latch the first unfired match and return its display name, or nullopt.
    std::optional<std::string> onAlloc(std::string_view typeName);
    std::optional<std::string> onThrow(std::string_view typeName);
    std::optional<std::string> onGc(int generation);

private:
    struct Trigger {
        Kind kind;
        std::string arg;              // type name, or minimum generation for Gc
        int minGen = 0;               // parsed from arg for Gc
        std::string display;
        std::atomic<bool> fired{false};
    };

    std::optional<std::string> matchType(Kind kind, std::string_view typeName);

    std::deque<Trigger> triggers_;    // deque: stable elements (atomics aren't movable)
    int alloc_ = 0, gc_ = 0, throw_ = 0;
};

} // namespace Sherlock
