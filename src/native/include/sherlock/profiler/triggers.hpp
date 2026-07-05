#pragma once

#include <atomic>
#include <deque>
#include <optional>
#include <string>
#include <string_view>

namespace Sherlock {

/// Non-call snapshot triggers: fire (once each) on an allocation of a type, a GC of a
/// generation, or a thrown exception type. `call:` triggers are handled separately by
/// ProbeManager (they need ReJIT); these ride callbacks the profiler already receives.
/// Matching is cheap so it's safe on the allocation hot path / GC thread; the actual
/// heap dump happens out-of-process in sl in response to the event we emit.
class SnapshotTriggers {
public:
    enum class Kind { Alloc, Gc, Throw };

    /// Arm a trigger. `arg` is a type name (Alloc/Throw) or a minimum generation
    /// (Gc, e.g. "gen2"; empty = any). `display` is what sl shows (e.g. "alloc:Customer").
    void add(Kind kind, std::string arg, std::string display);

    bool empty() const { return triggers_.empty(); }
    bool wantsAlloc() const { return alloc_ > 0; }
    bool wantsGc() const { return gc_ > 0; }
    bool wantsThrow() const { return throw_ > 0; }

    /// If an unfired Alloc/Throw trigger matches `typeName` (or a Gc trigger matches
    /// `generation`), latch it and return its display name. Otherwise nullopt.
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
