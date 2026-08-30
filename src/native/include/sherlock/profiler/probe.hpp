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
};

enum class ProbeEvents : std::uint8_t {
    None = 0,
    Enter = 1,
    Exit = 2,
    EnterAndExit = 3,
};

constexpr bool includes(ProbeEvents events, ProbePhase phase) {
    return (static_cast<std::uint8_t>(events) & static_cast<std::uint8_t>(phase)) != 0;
}

/// The immutable part of a method probe baked into rewritten IL. `cookie` points to
/// stable process-lifetime state, so the injected hook does not search an armed-method map.
struct ProbePlan {
    std::uintptr_t cookie = 0;
    ProbeEvents events = ProbeEvents::None;

    explicit operator bool() const { return cookie != 0 && events != ProbeEvents::None; }
    bool onEnter() const { return includes(events, ProbePhase::Enter); }
    bool onExit() const { return includes(events, ProbePhase::Exit); }
};

/// Thread-safe registry for resolved method probes. Map lookup happens only while rewriting IL;
/// an executing instrumented method calls dispatch() with its embedded state pointer.
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
        State(ProbeRegistry* owner, ModuleID module, mdMethodDef token,
              std::string display, ProbeEvents events)
            : owner(owner), module(module), token(token), display(std::move(display)),
              events(static_cast<std::uint8_t>(events)) {}

        ProbeRegistry* owner;
        ModuleID module;
        mdMethodDef token;
        std::string display;
        std::atomic<std::uint8_t> events;
        std::atomic<std::uint8_t> fired{0};
        std::atomic<bool> active{true};
    };

    mutable std::mutex mutex_;
    std::unordered_map<MethodKey, State*, MethodKeyHash> byMethod_;
    // States are never moved or reclaimed while the profiler is active: their addresses are
    // embedded in already-JITted code. Removing a probe only marks its state inactive.
    std::vector<std::unique_ptr<State>> states_;
    HitCallback onHit_;
};

extern "C" void Sherlock_ProbeEnter(std::intptr_t cookie);
extern "C" void Sherlock_ProbeExit(std::intptr_t cookie);

/// Resolves method "breakpoints" and requests targeted ReJIT.
///
/// This class does not rewrite IL. It resolves `Namespace.Type.Method` specs, registers
/// stable probe state, and asks the runtime to ReJIT only matching methods. The shared
/// shadow-stack instrumenter then emits the requested enter/exit hooks with its own rewrite.
class ProbeManager {
public:
    ProbeManager(ICorProfilerInfo10* info, Logger* logger);

    /// Parse "Ns.Type.Method;Ns.Other.Dispose,..." (';' or ',' separated).
    void configure(const std::string& spec, ProbeEvents events = ProbeEvents::Enter);
    bool empty() const;

    /// Arm a spec at runtime (from the REPL over the control channel): parse it and
    /// resolve against already-loaded modules, ReJITting matches now. Returns true if a
    /// method was armed (false = no match in a loaded module - e.g. not loaded yet).
    bool armLive(const std::string& spec, ProbeEvents events = ProbeEvents::Enter);

    void setHitCallback(ProbeRegistry::HitCallback callback) {
        registry_.setHitCallback(std::move(callback));
    }

    /// Resolve configured probes before the shadow-stack instrumenter requests ReJIT for
    /// every method in this newly loaded module.
    void onModuleLoaded(ModuleID moduleId);
    void onModuleUnloaded(ModuleID moduleId);

    /// Called by the shared IL rewriter. The lookup happens once per ReJIT, never per call.
    ProbePlan planFor(ModuleID moduleId, mdMethodDef token) const {
        return registry_.planFor(moduleId, token);
    }

private:
    struct Spec {
        std::string type;   // "Ns.Type"
        std::string method; // "Method"
        ProbeEvents events;
    };

    // Resolve specs against one module. Newly loaded modules are already about to be
    // globally ReJITted for the shadow stack; live arms request targeted ReJIT here.
    std::size_t resolveInModule(ModuleID moduleId, bool requestRejit);

    ICorProfilerInfo10* info_;
    Logger* logger_;

    mutable std::mutex mutex_;
    std::vector<ModuleID> loadedModules_; // for armLive() resolution against loaded modules
    std::vector<Spec> specs_;
    ProbeRegistry registry_;
};

} // namespace Sherlock
