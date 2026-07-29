#pragma once

#include <atomic>
#include <cstdint>
#include <string>
#include <unordered_map>
#include <vector>

#include "profilercommon.h"

namespace Sherlock {

class Logger;

// ---------------------------------------------------------------------------
// Thread-local shadow stack. Maintained by IL injected into every managed method
// (push on entry, pop in a finally). ObjectAllocated reads the top instead of
// calling DoStackSnapshot — O(1) per allocation vs O(stack depth).
//
// Exposed here so the profiler's ObjectAllocated can read it directly (no call
// through a vtable on the hot path).
// ---------------------------------------------------------------------------
namespace shadow {

constexpr std::size_t kMaxShadow = 1024; // frames retained; deeper is counted but not stored

// Per-thread shadow stack. The frame buffer is heap-allocated on first use and only a
// POINTER is kept in thread_local storage — a large in-TLS array would blow the dlopen
// static-TLS surplus and make the .so fail to load ("cannot allocate memory in static TLS
// block"). (The whole library is built -ftls-model=initial-exec, so this pointer is read
// via a direct thread-pointer offset on the hot push/pop path — see CMakeLists.txt.)
struct ThreadStack {
    FunctionID frames[kMaxShadow];
    std::uint32_t depth = 0; // may exceed kMaxShadow; readers clamp
};

extern thread_local ThreadStack* t_stack;

ThreadStack* ensureStack(); // allocate-on-first-use for the current thread

inline std::uint32_t storedDepth() {
    if (t_stack == nullptr) return 0;
    return t_stack->depth < kMaxShadow ? t_stack->depth : static_cast<std::uint32_t>(kMaxShadow);
}
inline const FunctionID* frames() {
    return t_stack ? t_stack->frames : nullptr;
}

} // namespace shadow

// The two trampolines the injected IL calls (unmanaged C calling convention, via
// calli). Global — no client data — mirroring probe.cpp's Sherlock_ProbeEnter.
extern "C" void Sherlock_ShadowPush(std::int64_t funcId);
extern "C" void Sherlock_ShadowPop();

// ---------------------------------------------------------------------------
// ShadowStackInstrumenter: rewrites a method's IL at JIT time to push its
// FunctionID on entry and pop it in a finally that wraps the whole body.
//
// Standalone (does NOT share code with ProbeManager — the IL plumbing was copied
// so the two can evolve independently). Degrades safely: any method it can't
// confidently rewrite is left with its original IL (returns without setting new
// body), so we never hand the runtime invalid IL.
// ---------------------------------------------------------------------------
class ShadowStackInstrumenter {
public:
    ShadowStackInstrumenter(ICorProfilerInfo10* info, Logger* logger);

    // --- ReJIT instrumentation (ModuleLoadFinished -> RequestReJIT -> GetReJITParameters) ---
    // Enumerate every method in a freshly loaded module and request a ReJIT for each, so the
    // rewritten IL applies from first call AND is used for inlined bodies (inline-aware) —
    // letting us keep inlining ON.
    void onModuleLoaded(ModuleID moduleId);
    // Deliver the rewritten IL for one ReJIT request. Returns S_OK regardless (a skip leaves
    // the original IL). Resolves the FunctionID from the token to bake into the push.
    HRESULT getReJITParameters(ModuleID moduleId, mdMethodDef methodToken,
                               ICorProfilerFunctionControl* control);

    std::uint64_t instrumentedCount() const { return instrumented_; }
    std::uint64_t skippedCount() const { return skipped_; }

    // Circuit breaker. The runtime reports a rewrite the JIT rejected via ReJITError; the profiler routes
    // it here. After too many, we latch OFF permanently — every subsequent getReJITParameters leaves the
    // original IL untouched. Rewriting arbitrary framework IL is the profiler's biggest in-process risk
    // (bad IL corrupts the customer's process), so if our rewrites start failing we stop rather than keep
    // handing the runtime bodies it dislikes. One-way latch: we never re-enable within a process.
    void noteReJITError();
    bool disabled() const { return disabled_; }

private:
    // Per-module standalone sig tokens for the push/pop trampolines (cached).
    struct ModuleSigs { mdSignature push = mdSignatureNil; mdSignature pop = mdSignatureNil; };
    ModuleSigs& ensureSigs(ModuleID moduleId);

    // Core IL rewrite: builds the try/finally-wrapped body with push/pop into `out`. Returns
    // true on success (out filled), false to skip (leave original IL). Shared by both paths.
    bool buildIL(FunctionID functionId, ModuleID moduleId, mdMethodDef methodToken,
                 std::vector<BYTE>& out);

    // After this many ReJITErrors we latch the instrumenter off (see noteReJITError).
    static constexpr std::uint64_t kMaxReJITErrors = 10;

    ICorProfilerInfo10* info_;
    Logger* logger_;
    std::unordered_map<std::uint64_t, ModuleSigs> sigByModule_;
    std::uint64_t instrumented_ = 0;
    std::uint64_t skipped_ = 0;
    std::atomic<std::uint64_t> rejitErrors_{0};
    std::atomic<bool> disabled_{false};
};

} // namespace Sherlock
