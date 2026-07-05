#pragma once

#include <atomic>
#include <cstdint>
#include <deque>
#include <functional>
#include <string>
#include <unordered_map>
#include <vector>

#include "profilercommon.h"

namespace Sherlock {

class Logger;

/// Method "breakpoints" via ReJIT + IL rewriting.
///
/// Each `Namespace.Type.Method` spec (from SHERLOCK_BREAK) is re-JITted with a tiny
/// prologue spliced into its IL: a `calli` into a native trampoline. The first time the
/// method is entered, the trampoline fires a callback — the profiler turns that into a
/// "snapshot now" event to sl. This is *only* a remote trigger: it records nothing itself
/// (no stacks, no aggregation). All provenance comes from the heap snapshot that follows,
/// so a missed trigger (inlined / tiny forwarder / NativeAOT) just means no dump at that
/// instant — it can never corrupt an analysis.
class ProbeManager {
public:
    ProbeManager(ICorProfilerInfo10* info, Logger* logger);

    /// Parse "Ns.Type.Method;Ns.Other.Dispose,..." (';' or ',' separated).
    void configure(const std::string& spec);
    bool empty() const { return specs_.empty(); }

    /// Arm a spec at runtime (from the REPL over the control channel): parse it and
    /// resolve against already-loaded modules, ReJITting matches now. Returns true if a
    /// method was armed (false = no match in a loaded module — e.g. not loaded yet).
    bool armLive(const std::string& spec);

    /// Called once per probe, the first time it fires, with its display name. The profiler
    /// routes this to sl over the control channel as a probe-hit event (→ heap snapshot).
    void setHitCallback(std::function<void(const std::string&)> cb) { onHit_ = std::move(cb); }

    /// Resolve any pending specs that live in this freshly-loaded module and
    /// RequestReJIT the matching methods.
    void onModuleLoaded(ModuleID moduleId);

    /// ReJIT callback: hand the runtime the rewritten IL for an armed method.
    HRESULT getReJITParameters(ModuleID moduleId, mdMethodDef methodId, ICorProfilerFunctionControl* control);

    /// True if (module, token) is armed — used to forbid inlining so the probe fires.
    bool isArmed(ModuleID moduleId, mdMethodDef token) const;

    /// Called from the injected IL (via the global trampoline) on every hit.
    void onProbeHit(std::int32_t probeId);

private:
    struct Spec {
        std::string type;   // "Ns.Type"
        std::string method; // "Method"
        bool resolved = false;
    };

    struct Armed {
        ModuleID module;
        mdMethodDef token;
        std::int32_t probeId;
        std::string display; // "Ns.Type.Method"
    };

    // Per-module standalone signature token for the native trampoline (cached).
    mdSignature ensureProbeSig(ModuleID moduleId);

    // Resolve unresolved specs against one module and ReJIT the matches.
    void resolveInModule(ModuleID moduleId);

    ICorProfilerInfo10* info_;
    Logger* logger_;

    std::vector<ModuleID> loadedModules_; // for armLive() resolution against loaded modules
    std::vector<Spec> specs_;
    std::deque<Armed> armed_;                                    // index == probeId; deque keeps refs stable
    std::deque<std::atomic<bool>> fired_;                        // per-probe "already fired" latch
    std::unordered_map<std::uint64_t, mdSignature> sigByModule_; // ModuleID -> sig token

    std::function<void(const std::string&)> onHit_;
};

} // namespace Sherlock
