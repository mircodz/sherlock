#pragma once

#include <atomic>
#include <cstdint>
#include <map>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "profilercommon.h"
#include "sherlock/profiler/method_registry.hpp"

namespace Sherlock {

class Logger;
struct ProbePlan;

// ReJIT-injected IL pushes on entry and pops in a finally. ObjectAllocated reads
// these profiler-owned FrameIds directly instead of walking the CLR stack.
namespace shadow {

constexpr std::size_t kInitialShadow = 1024;    // frames a thread's buffer starts with; it doubles as calls nest
constexpr std::size_t kMaxShadow = 256 * 1024;  // frames retained; deeper than any managed stack, counted but not stored

// Keep only a pointer in initial-exec TLS: an inline frame array can exhaust dlopen's static-TLS surplus. Only the
// owning thread pushes, pops, and reads its stack, so growing it needs no synchronization.
struct ThreadStack {
    std::vector<FrameId> frames = std::vector<FrameId>(kInitialShadow);
    std::uint32_t depth = 0;     // may exceed the stored frames; readers clamp
    bool growable = true;        // false once growing failed, so no frame is skipped below the stored depth
};

extern thread_local ThreadStack* t_stack;

ThreadStack* ensureStack() noexcept; // allocate-on-first-use for the current thread; null when out of memory
std::size_t liveStacks() noexcept;   // threads holding a stack; each is freed when its thread exits

inline std::uint32_t storedDepth() {
    if (t_stack == nullptr) return 0;
    return t_stack->depth < t_stack->frames.size() ? t_stack->depth : static_cast<std::uint32_t>(t_stack->frames.size());
}
inline const FrameId* frames() {
    return t_stack ? t_stack->frames.data() : nullptr;
}

} // namespace shadow

// Called by injected IL via unmanaged C calli.
extern "C" void Sherlock_ShadowPush(std::int64_t frameId) noexcept;
extern "C" void Sherlock_ShadowPop() noexcept;

// The sole IL rewriter, composing shadow-stack and optional probe hooks. Methods of ordinary modules are rewritten
// through ReJIT; ReJIT can't reach dynamic (Reflection.Emit) or collectible modules, so theirs are rewritten when
// first JIT-compiled. Each module takes one path, so no method is wrapped twice.
class ShadowStackInstrumenter {
public:
    ShadowStackInstrumenter(ICorProfilerInfo10* info, Logger* logger, MethodRegistry& methods);

    // Request ReJIT for every method in a newly loaded module, or note a dynamic or collectible one for JIT time;
    // inlining stays enabled.
    void onModuleLoaded(ModuleID moduleId);
    // False leaves the original IL untouched so an armed probe can report failure.
    bool rewrite(ModuleID moduleId, mdMethodDef methodToken, const ProbePlan& probe, ICorProfilerFunctionControl* control);
    // Rewrites a method of a dynamic or collectible module before its first compilation; later tiers reuse that IL.
    void onJitCompilationStarted(ModuleID moduleId, mdMethodDef methodToken, const ProbePlan& probe);
    void onModuleUnloaded(ModuleID moduleId);

    std::uint64_t instrumentedCount() const { return instrumented_.load(std::memory_order_relaxed); }
    std::uint64_t skippedCount() const { return skipped_.load(std::memory_order_relaxed); }
    // "12 it returns by reference; 3 it uses tail., …": how many methods were skipped for each reason.
    std::string skipSummary() const;

    // Repeated JIT rejections permanently disable further rewrites to protect the process.
    void noteReJITError();

private:
    struct ModuleSigs {
        mdSignature push = mdSignatureNil;
        mdSignature pop = mdSignatureNil;
        mdSignature probe = mdSignatureNil;
    };
    ModuleSigs ensureSigs(ModuleID moduleId);

    // Builds one try/finally-wrapped body containing shadow-stack and optional probe hooks. Returns why it
    // couldn't, or null.
    const char* buildIL(FrameId frameId, ModuleID moduleId, mdMethodDef methodToken,
                        const ProbePlan& probe, std::vector<BYTE>& out);
    // The rewritten body, or why there is none (already counted as skipped).
    const char* prepare(ModuleID moduleId, mdMethodDef methodToken, const ProbePlan& probe, std::vector<BYTE>& out);
    void skip(mdMethodDef methodToken, const char* reason);

    static constexpr std::uint64_t kMaxReJITErrors = 10;

    ICorProfilerInfo10* info_;
    Logger* logger_;
    MethodRegistry& methods_;
    std::mutex sigMutex_;
    std::unordered_map<std::uint64_t, ModuleSigs> sigByModule_;
    // Dynamic and collectible modules, with the methods already rewritten at JIT time.
    std::mutex jitMutex_;
    std::unordered_map<ModuleID, std::unordered_set<mdMethodDef>> jitModules_;
    std::atomic<std::uint64_t> instrumented_{0};
    std::atomic<std::uint64_t> skipped_{0};
    mutable std::mutex skipMutex_;
    std::map<std::string, std::uint64_t> skipReasons_;
    std::atomic<std::uint64_t> rejitErrors_{0};
    std::atomic<bool> disabled_{false};
};

} // namespace Sherlock
