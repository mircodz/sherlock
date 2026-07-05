#pragma once

#include <atomic>
#include <cstdint>
#include <deque>
#include <functional>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "profilercommon.h"

namespace Sherlock {

class Logger;

/// Method "breakpoints" via ReJIT + IL rewriting (MVP: trace-only).
///
/// Given a set of `Namespace.Type.Method` specs (from SHERLOCK_BREAK), each
/// matching method is re-JITted with a tiny prologue spliced into its IL: a
/// `calli` into a native trampoline that records the call. So every time the
/// method is entered we capture the managed stack and fold it by hit count —
/// "who calls Dispose", without an interactive debugger.
///
/// This is observe-and-react, not pause-and-step: the probe records and returns.
/// Pausing/conditions are deliberately out of this MVP.
class ProbeManager {
public:
    ProbeManager(ICorProfilerInfo10* info, Logger* logger);

    /// Parse "Ns.Type.Method[:action];..." (';' or ',' separated). The optional
    /// action (default "trace") is "snapshot" to also signal a heap dump on first hit.
    void configure(const std::string& spec);
    bool empty() const { return specs_.empty(); }

    /// Called (once per probe) when a snapshot-action probe first fires, with its display
    /// name. The profiler routes this to sl over the control channel as a probe-hit event.
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

    /// Write folded stacks weighted by hit count, using `symbolize` for frame names.
    void dump(const std::string& path, const std::function<std::string(FunctionID)>& symbolize);

private:
    struct Spec {
        std::string type;   // "Ns.Type"
        std::string method; // "Method"
        bool snapshot = false; // action: capture a heap dump on first hit
        bool resolved = false;
    };

    struct Armed {
        ModuleID module;
        mdMethodDef token;
        std::int32_t probeId;
        bool snapshot;
        std::string display; // "Ns.Type.Method"
    };

    // A distinct captured call path (leaf -> root) and how often the probe hit it.
    struct PathStat {
        std::vector<FunctionID> frames;
        std::uint64_t hits = 0;
    };

    // Per-module standalone signature token for the native trampoline (cached).
    mdSignature ensureProbeSig(ModuleID moduleId);

    ICorProfilerInfo10* info_;
    Logger* logger_;

    std::vector<Spec> specs_;
    std::deque<Armed> armed_;                                   // index == probeId; deque keeps refs stable
    std::deque<std::atomic<bool>> snapshotSignaled_;            // per-probe "already signalled" latch
    std::unordered_map<std::uint64_t, mdSignature> sigByModule_; // ModuleID -> sig token

    std::function<void(const std::string&)> onHit_;

    std::mutex hitsMutex_;
    std::unordered_map<std::uint64_t, PathStat> hits_;         // keyed by stack hash
};

} // namespace Sherlock
