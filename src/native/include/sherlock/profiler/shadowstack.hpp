#pragma once

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "profilercommon.h"
#include "sherlock/profiler/method_registry.hpp"

namespace Sherlock {

class Logger;
struct ProbePlan;

// ReJIT-injected IL pushes on entry and pops in a finally. ObjectAllocated reads
// these profiler-owned FrameIds directly instead of walking the CLR stack.
namespace shadow {

constexpr std::size_t kMaxShadow = 1024; // frames retained; deeper is counted but not stored

// Keep only a pointer in initial-exec TLS: an inline frame array can exhaust
// dlopen's static-TLS surplus. Each thread allocates its buffer on first use.
struct ThreadStack {
    FrameId frames[kMaxShadow];
    std::uint32_t depth = 0; // may exceed kMaxShadow; readers clamp
};

extern thread_local ThreadStack* t_stack;

ThreadStack* ensureStack(); // allocate-on-first-use for the current thread

inline std::uint32_t storedDepth() {
    if (t_stack == nullptr) return 0;
    return t_stack->depth < kMaxShadow ? t_stack->depth : static_cast<std::uint32_t>(kMaxShadow);
}
inline const FrameId* frames() {
    return t_stack ? t_stack->frames : nullptr;
}

} // namespace shadow

// Called by injected IL via unmanaged C calli.
extern "C" void Sherlock_ShadowPush(std::int64_t frameId);
extern "C" void Sherlock_ShadowPop();

// The sole ReJIT rewriter, composing shadow-stack and optional probe hooks.
class ShadowStackInstrumenter {
public:
    ShadowStackInstrumenter(ICorProfilerInfo10* info, Logger* logger, MethodRegistry& methods);

    // Request ReJIT for every method in a newly loaded module; inlining stays enabled.
    void onModuleLoaded(ModuleID moduleId);
    // False leaves the original IL untouched so an armed probe can report failure.
    bool rewrite(ModuleID moduleId, mdMethodDef methodToken, const ProbePlan& probe, ICorProfilerFunctionControl* control);
    void onModuleUnloaded(ModuleID moduleId);

    std::uint64_t instrumentedCount() const { return instrumented_.load(std::memory_order_relaxed); }
    std::uint64_t skippedCount() const { return skipped_.load(std::memory_order_relaxed); }

    // Repeated JIT rejections permanently disable further rewrites to protect the process.
    void noteReJITError();

private:
    struct ModuleSigs {
        mdSignature push = mdSignatureNil;
        mdSignature pop = mdSignatureNil;
        mdSignature probe = mdSignatureNil;
    };
    ModuleSigs ensureSigs(ModuleID moduleId);

    // Builds one try/finally-wrapped body containing shadow-stack and optional probe hooks.
    bool buildIL(FrameId frameId, ModuleID moduleId, mdMethodDef methodToken,
                 const ProbePlan& probe, std::vector<BYTE>& out);

    static constexpr std::uint64_t kMaxReJITErrors = 10;

    ICorProfilerInfo10* info_;
    Logger* logger_;
    MethodRegistry& methods_;
    std::mutex sigMutex_;
    std::unordered_map<std::uint64_t, ModuleSigs> sigByModule_;
    std::atomic<std::uint64_t> instrumented_{0};
    std::atomic<std::uint64_t> skipped_{0};
    std::atomic<std::uint64_t> rejitErrors_{0};
    std::atomic<bool> disabled_{false};
};

} // namespace Sherlock
