#include "sherlock/profiler/triggers.hpp"

#include <cstdlib>

namespace Sherlock {

void SnapshotTriggers::add(Kind kind, std::string arg, std::string display) {
    auto trigger = std::make_unique<Trigger>();
    trigger->kind = kind;
    if (kind == Kind::Gc && !arg.empty()) {
        const char* p = arg.c_str();
        if (arg.rfind("gen", 0) == 0) p += 3; // accept "gen2" or "2"
        trigger->minGen = std::atoi(p);
    }
    trigger->arg = std::move(arg);
    trigger->display = std::move(display);

    std::lock_guard lock(addMutex_);
    auto next = std::make_unique<List>();
    if (!lists_.empty()) {
        *next = *lists_.back();
    }
    next->push_back(trigger.get());
    triggers_.push_back(std::move(trigger));
    // Count it before publishing, so the count is never below the unfired triggers a callback can see.
    unfired_[static_cast<int>(kind)].fetch_add(1, std::memory_order_relaxed);
    list_.store(next.get(), std::memory_order_release);
    lists_.push_back(std::move(next));
}

std::optional<std::string> SnapshotTriggers::fire(Trigger& trigger) {
    if (trigger.fired.exchange(true, std::memory_order_acq_rel)) {
        return std::nullopt; // another thread fired it first
    }
    unfired_[static_cast<int>(trigger.kind)].fetch_sub(1, std::memory_order_relaxed);
    return trigger.display;
}

std::optional<std::string> SnapshotTriggers::matchType(Kind kind, std::string_view typeName) {
    const List* list = list_.load(std::memory_order_acquire);
    if (list == nullptr) {
        return std::nullopt;
    }
    for (Trigger* t : *list) {
        if (t->kind != kind || t->fired.load(std::memory_order_relaxed)) {
            continue;
        }
        // Throw with an empty arg matches any exception; otherwise exact type match.
        const bool matches = (t->arg.empty() && kind == Kind::Throw) || t->arg == typeName;
        if (matches) {
            if (auto display = fire(*t)) {
                return display;
            }
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
    const List* list = list_.load(std::memory_order_acquire);
    if (list == nullptr) {
        return std::nullopt;
    }
    for (Trigger* t : *list) {
        if (t->kind == Kind::Gc && generation >= t->minGen && !t->fired.load(std::memory_order_relaxed)) {
            if (auto display = fire(*t)) {
                return display;
            }
        }
    }
    return std::nullopt;
}

} // namespace Sherlock
