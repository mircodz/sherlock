#pragma once

#include <array>
#include <atomic>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace Sherlock {

// One-shot alloc/gc/throw triggers run in CLR callbacks; sl captures the heap out of process.
// ProbeManager handles call triggers through ReJIT.
//
// add() may run on the control thread while CLR callbacks match on other threads, so matching reads an immutable
// list published through an atomic pointer, and arming publishes a new one. Lists and triggers live as long as this
// object: arming is rare, so nothing is reclaimed earlier.
class SnapshotTriggers {
public:
    enum class Kind { Alloc, Gc, Throw };

    // arg is a type name or minimum GC generation ("gen2"; empty = any).
    // display is the label sent to sl.
    void add(Kind kind, std::string arg, std::string display);

    // Whether a trigger of this kind is still unfired, so callbacks can skip matching (and resolving type names)
    // once every trigger has fired.
    bool wantsAlloc() const { return unfired(Kind::Alloc) > 0; }
    bool wantsGc() const { return unfired(Kind::Gc) > 0; }
    bool wantsThrow() const { return unfired(Kind::Throw) > 0; }

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
    using List = std::vector<Trigger*>;

    int unfired(Kind kind) const { return unfired_[static_cast<int>(kind)].load(std::memory_order_relaxed); }
    std::optional<std::string> fire(Trigger& trigger);
    std::optional<std::string> matchType(Kind kind, std::string_view typeName);

    std::atomic<const List*> list_{nullptr};
    std::array<std::atomic<int>, 3> unfired_{};

    std::mutex addMutex_;                        // serializes add(); matching never takes it
    std::vector<std::unique_ptr<Trigger>> triggers_;
    std::vector<std::unique_ptr<const List>> lists_; // every list ever published, the current one last
};

} // namespace Sherlock
