#pragma once

#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "profilercommon.h"

namespace Sherlock {

class Logger;

enum class ProbePhase : std::uint8_t {
    Enter = 1,
    Exit = 2,
    Return = 4,
};

enum class ProbeEvents : std::uint8_t {
    None = 0,
    Enter = 1,
    Exit = 2,
    EnterAndExit = 3,
    Return = 4,
};

constexpr bool includes(ProbeEvents events, ProbePhase phase) {
    return (static_cast<std::uint8_t>(events) & static_cast<std::uint8_t>(phase)) != 0;
}

// Baked into IL. cookie points to stable state, avoiding map lookups in injected hooks.
struct ProbePlan {
    std::uintptr_t cookie = 0;
    ProbeEvents events = ProbeEvents::None;

    explicit operator bool() const { return cookie != 0 && events != ProbeEvents::None; }
    bool onEnter() const { return includes(events, ProbePhase::Enter); }
    bool onExit() const { return includes(events, ProbePhase::Exit); }
    bool onReturn() const { return includes(events, ProbePhase::Return); }
};

// Map lookups happen during rewriting; injected hooks dispatch through embedded state pointers.
class ProbeRegistry {
public:
    using HitCallback = std::function<void(const std::string&, ProbePhase)>;

    struct Registration {
        ProbePlan plan;
        bool changed = false;
        bool inserted = false;
    };

    Registration registerMethod(
        ModuleID module, mdMethodDef token, std::string display, ProbeEvents events);
    ProbePlan planFor(ModuleID module, mdMethodDef token) const;
    void removeMethod(ModuleID module, mdMethodDef token, std::uintptr_t cookie);
    void removeModule(ModuleID module);

    // Set during profiler initialization, before modules can execute instrumented code.
    void setHitCallback(HitCallback callback) { onHit_ = std::move(callback); }

    static void dispatch(std::uintptr_t cookie, ProbePhase phase) noexcept;

private:
    struct MethodKey {
        ModuleID module;
        mdMethodDef token;

        bool operator==(const MethodKey&) const = default;
    };

    struct MethodKeyHash {
        std::size_t operator()(const MethodKey& key) const noexcept;
    };

    struct State {
        State(ProbeRegistry* owner, std::string display, ProbeEvents events)
            : owner(owner), display(std::move(display)),
              events(static_cast<std::uint8_t>(events)) {}

        ProbeRegistry* owner;
        std::string display;
        std::atomic<std::uint8_t> events;
        std::atomic<std::uint8_t> fired{0};
        std::atomic<bool> active{true};
    };

    mutable std::mutex mutex_;
    std::unordered_map<MethodKey, State*, MethodKeyHash> byMethod_;
    // JITted code embeds these addresses. Removal deactivates state without reclaiming it.
    std::vector<std::unique_ptr<State>> states_;
    HitCallback onHit_;
};

extern "C" void Sherlock_ProbeEnter(std::intptr_t cookie);
extern "C" void Sherlock_ProbeExit(std::intptr_t cookie);
extern "C" void Sherlock_ProbeReturn(std::intptr_t cookie);

// Resolves Namespace.Type.Method specs and requests ReJIT; ShadowStackInstrumenter emits the hooks.
class ProbeManager {
public:
    ProbeManager(ICorProfilerInfo10* info, Logger* logger);

    // Specs are separated by ';' or ','.
    void configure(const std::string& spec, ProbeEvents events = ProbeEvents::Enter);

    // Resolve against loaded modules and request ReJIT. True if a method was armed.
    bool armLive(const std::string& spec, ProbeEvents events = ProbeEvents::Enter);
    ProbePlan registerMethod(
        ModuleID moduleId,
        mdMethodDef token,
        std::string display,
        ProbeEvents events);

    void setHitCallback(ProbeRegistry::HitCallback callback) {
        registry_.setHitCallback(std::move(callback));
    }

    // Resolve probes before the module-wide shadow-stack ReJIT request.
    void onModuleLoaded(ModuleID moduleId);
    void onModuleUnloaded(ModuleID moduleId);

    // Lookup once per ReJIT, never per managed call.
    ProbePlan planFor(ModuleID moduleId, mdMethodDef token) const {
        return registry_.planFor(moduleId, token);
    }

private:
    struct Spec {
        std::string type;   // "Ns.Type"
        std::string method; // "Method"
        ProbeEvents events;
    };

    std::vector<Spec> configureSpecs(const std::string& spec, ProbeEvents events);
    // Module loads already request shadow-stack ReJIT; live arms need targeted requests.
    std::size_t resolveInModule(ModuleID moduleId, bool requestRejit, const std::vector<Spec>& specs);

    ICorProfilerInfo10* info_;
    Logger* logger_;

    mutable std::mutex mutex_;
    std::vector<ModuleID> loadedModules_; // for armLive() resolution against loaded modules
    std::vector<Spec> specs_;
    ProbeRegistry registry_;
};

} // namespace Sherlock
