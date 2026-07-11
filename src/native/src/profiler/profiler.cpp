// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#include "sherlock/profiler/profiler.hpp"

#include "sherlock/control/protocol.hpp"

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#ifdef _WIN32
#include <process.h> // _getpid
#else
#include <unistd.h> // getpid
#endif

namespace Sherlock {

namespace {

// Cap stack depth so pathological recursion can't blow up the hot path or the
// aggregation key.
constexpr std::size_t kMaxFrames = 64;

// Insert the current pid before the extension (allocations.tsv -> allocations.<pid>.tsv),
// so every profiled process - including children that inherit the env - writes a
// distinct file instead of clobbering a shared one.
std::string withPid(const std::string& path) {
#ifdef _WIN32
    int pid = _getpid();
#else
    int pid = getpid();
#endif
    std::string suffix = "." + std::to_string(pid);
    std::size_t slash = path.find_last_of("/\\");
    std::size_t dot = path.find_last_of('.');
    if (dot == std::string::npos || (slash != std::string::npos && dot < slash)) {
        return path + suffix; // no extension
    }
    return path.substr(0, dot) + suffix + path.substr(dot);
}

// Filled by captureStack during a walk (leaf -> root), read right after. Thread-
// local so concurrent allocating threads don't clobber each other; capacity is
// retained between allocations to avoid reallocation.
thread_local std::vector<FunctionID> t_frames;

// Bytes allocated on this thread since the last sample was taken.
thread_local std::uint64_t t_bytesSinceSample = 0;

// DoStackSnapshot callback: collect managed frames (funcId == 0 marks native /
// runtime frames, which we skip) up to kMaxFrames.
HRESULT __stdcall captureStack(FunctionID funcId, UINT_PTR, COR_PRF_FRAME_INFO, ULONG32, BYTE[], void*) {
    if (funcId != 0) {
        t_frames.push_back(funcId);
        if (t_frames.size() >= kMaxFrames)
            return E_ABORT; // got enough depth; any non-S_OK ends the walk
    }
    return S_OK;
}

// Single tracer per process; the ELT hooks are global function pointers with no
// client data, so they reach it through this.
TraceCollector* g_trace = nullptr;

// ELT2 hooks - the canonical CoreCLR slow-path variant (the runtime saves/restores
// registers around these, so they can be plain C functions). We only use funcId.
void STDMETHODCALLTYPE EnterHook(FunctionID funcId, UINT_PTR, COR_PRF_FRAME_INFO, COR_PRF_FUNCTION_ARGUMENT_INFO*) {
    if (g_trace) g_trace->onEnter(funcId);
}
void STDMETHODCALLTYPE LeaveHook(FunctionID funcId, UINT_PTR, COR_PRF_FRAME_INFO, COR_PRF_FUNCTION_ARGUMENT_RANGE*) {
    if (g_trace) g_trace->onLeave(funcId);
}
void STDMETHODCALLTYPE TailcallHook(FunctionID funcId, UINT_PTR, COR_PRF_FRAME_INFO) {
    if (g_trace) g_trace->onLeave(funcId); // a tailcall leaves the current frame
}

} // namespace

Profiler::Profiler()
    : logger(std::make_unique<Logger>()) {
}

Profiler::~Profiler() {
    if (corProfilerInfo != nullptr) {
        corProfilerInfo->Release();
        corProfilerInfo = nullptr;
    }
}

HRESULT STDMETHODCALLTYPE Profiler::QueryInterface(REFIID riid, void** ppInterface) {
    if (riid == __uuidof(ICorProfilerCallback8) ||
        riid == __uuidof(ICorProfilerCallback7) ||
        riid == __uuidof(ICorProfilerCallback6) ||
        riid == __uuidof(ICorProfilerCallback5) ||
        riid == __uuidof(ICorProfilerCallback4) ||
        riid == __uuidof(ICorProfilerCallback3) ||
        riid == __uuidof(ICorProfilerCallback2) ||
        riid == __uuidof(ICorProfilerCallback) ||
        riid == IID_IUnknown) {
        *ppInterface = static_cast<ICorProfilerCallback8*>(this);
        AddRef();
        return S_OK;
    }
    *ppInterface = nullptr;
    return E_NOINTERFACE;
}

ULONG STDMETHODCALLTYPE Profiler::Release() {
    ULONG count = --refCount;
    if (count == 0) {
        delete this;
    }
    return count;
}

HRESULT STDMETHODCALLTYPE Profiler::Initialize(IUnknown* pICorProfilerInfoUnk) {
    HRESULT hr = pICorProfilerInfoUnk->QueryInterface(IID_ICorProfilerInfo10, (void**)&corProfilerInfo);
    if (FAILED(hr)) {
        logger->logError("QueryInterface for ICorProfilerInfo10 failed");
        return hr;
    }

    const char* traceEnv = std::getenv("SHERLOCK_TRACE");
    traceCalls = traceEnv != nullptr && traceEnv[0] != '\0' && traceEnv[0] != '0';

    const char* triggerEnv = std::getenv("SHERLOCK_SNAPSHOT_ON");
    bool hasStartupTriggers = triggerEnv != nullptr && triggerEnv[0] != '\0';
    const char* ctlSocketEnv = std::getenv("SHERLOCK_CONTROL_SOCKET");
    bool controlPresent = ctlSocketEnv != nullptr && ctlSocketEnv[0] != '\0';
    // Snapshot triggers are possible if pre-armed at startup, or if the control channel
    // lets the REPL arm them live.
    bool triggersEnabled = hasStartupTriggers || controlPresent;

    // Allocation tracking is always on; tracing adds ELT on top when requested.
    DWORD eventMask = COR_PRF_MONITOR_OBJECT_ALLOCATED |
                      COR_PRF_ENABLE_OBJECT_ALLOCATED |
                      COR_PRF_ENABLE_STACK_SNAPSHOT |
                      COR_PRF_MONITOR_GC; // GC callbacks for survivor tracking + gc: triggers
    if (traceCalls)
        eventMask |= COR_PRF_MONITOR_ENTERLEAVE;
    if (triggersEnabled)
        // ReJIT + module loads for call: triggers; exceptions for throw: triggers. No global
        // inline-disable - call: triggers simply don't fire on inlined/tiny methods (documented).
        eventMask |= COR_PRF_ENABLE_REJIT | COR_PRF_MONITOR_MODULE_LOADS | COR_PRF_MONITOR_EXCEPTIONS;

    hr = corProfilerInfo->SetEventMask(eventMask);
    if (FAILED(hr)) {
        logger->logError("SetEventMask failed");
        return hr;
    }

    const char* out = std::getenv("SHERLOCK_PROFILE_OUT");
    outputPath = withPid((out != nullptr && out[0] != '\0') ? out : "sherlock-allocations.txt");

    const char* sample = std::getenv("SHERLOCK_SAMPLE_BYTES");
    if (sample != nullptr && sample[0] != '\0')
        sampleInterval = std::strtoull(sample, nullptr, 10);

    aggregator = std::make_unique<Aggregator>(corProfilerInfo, logger.get());

    const char* correlateEnv = std::getenv("SHERLOCK_CORRELATE");
    correlate = correlateEnv != nullptr && correlateEnv[0] != '\0' && correlateEnv[0] != '0';
    if (correlate) {
        aggregator->enableCorrelation();
        const char* corrOut = std::getenv("SHERLOCK_CORRELATE_OUT");
        correlationPath = withPid((corrOut != nullptr && corrOut[0] != '\0') ? corrOut : "sherlock-correlation.txt");
    }


    if (traceCalls) {
        const char* traceOut = std::getenv("SHERLOCK_TRACE_OUT");
        tracePath = (traceOut != nullptr && traceOut[0] != '\0') ? traceOut : "sherlock-trace.txt";
        trace = std::make_unique<TraceCollector>(logger.get());
        g_trace = trace.get();
        trace->start();
        hr = corProfilerInfo->SetEnterLeaveFunctionHooks2(EnterHook, LeaveHook, TailcallHook);
        if (FAILED(hr)) {
            char buf[16];
            std::snprintf(buf, sizeof buf, "0x%08x", static_cast<unsigned>(hr));
            logger->logError(std::string("SetEnterLeaveFunctionHooks2 failed ") + buf);
        }
    }

    if (triggersEnabled) {
        probes = std::make_unique<ProbeManager>(corProfilerInfo, logger.get());
        triggers = std::make_unique<SnapshotTriggers>();
        if (hasStartupTriggers) {
            std::string spec = triggerEnv;
            std::size_t start = 0;
            while (start <= spec.size()) {
                std::size_t end = spec.find_first_of(";,", start);
                std::string one = spec.substr(start, end == std::string::npos ? std::string::npos : end - start);
                start = (end == std::string::npos) ? spec.size() + 1 : end + 1;
                while (!one.empty() && (one.front() == ' ' || one.front() == '\t')) one.erase(one.begin());
                while (!one.empty() && (one.back() == ' ' || one.back() == '\t')) one.pop_back();
                if (!one.empty()) armTrigger(one, false);
            }
            logger->logInfo(std::string("snapshot-on: ") + triggerEnv);
        }
    }

    // Control channel: connect to sl if a socket was provided. This is the unified
    // sl<->profiler channel for on-demand requests (emit-correlation, flush-allocations,
    // arm-trigger) and pushes events (snapshot triggers).
    if (controlPresent) {
        control = std::make_unique<control::ControlChannel>(logger.get());
        if (std::optional<std::string> err = control->connect(ctlSocketEnv)) {
            logger->logError("control channel connect failed: " + *err);
            control.reset();
        } else {
            std::vector<std::string> features = {"allocations"};
            if (correlate) features.push_back("correlate");
            if (triggersEnabled) features.push_back("snapshot-triggers");
            control->start("0.1", features,
                           [this](std::string_view cmd, std::span<const std::string_view> args) {
                               return handleControl(cmd, args);
                           });
            // Route call: trigger hits to sl as snapshot-trigger events over the channel.
            if (probes) {
                probes->setHitCallback([this](const std::string& name) { fireTrigger("call:" + name); });
            }
            logger->logInfo("control channel connected");
        }
    }

    isInitialized = true;

    logger->logInfo(traceCalls
        ? "profiler initialized; allocations by call stack + per-method tracing"
        : "profiler initialized; aggregating allocations by call stack");
    return S_OK;
}

HRESULT STDMETHODCALLTYPE Profiler::InitializeForAttach(IUnknown* pICorProfilerInfoUnk, void*, UINT) {
    return Initialize(pICorProfilerInfoUnk);
}

control::Reply Profiler::handleControl(std::string_view cmd, std::span<const std::string_view> args) {
    if (cmd == control::commands::kPing) {
        return control::Reply::success("pong");
    }
    if (cmd == control::commands::kEmitCorrelation) {
        if (!correlate || !aggregator) {
            return control::Reply::error("correlation not enabled for this run");
        }
        if (corProfilerInfo != nullptr) {
            corProfilerInfo->ForceGC(); // settle addresses before emitting
        }
        aggregator->emitCorrelation(correlationPath);
        // Return the GC count at emit; sl re-checks after the dump to detect drift (a GC
        // between emit and dump would move objects and invalidate the address join).
        return control::Reply::success(correlationPath + "\t" + std::to_string(gcCount.load()));
    }
    if (cmd == control::commands::kGcCount) {
        return control::Reply::success(std::to_string(gcCount.load()));
    }
    if (cmd == control::commands::kFlushAllocations) {
        if (!aggregator) {
            return control::Reply::error("no aggregator");
        }
        aggregator->dump(outputPath);
        return control::Reply::success(outputPath);
    }
    if (cmd == control::commands::kArmTrigger) {
        if (args.empty()) {
            return control::Reply::error("arm-trigger needs a <kind:arg> spec");
        }
        return armTrigger(std::string(args[0]), /*live=*/true)
            ? control::Reply::success("armed")
            : control::Reply::error("could not arm (unknown kind, or method not loaded yet)");
    }
    return control::Reply::error("unknown command");
}

bool Profiler::armTrigger(const std::string& spec, bool live) {
    // Parse "kind:arg"; a bare "Ns.Type.Method" is shorthand for "call:".
    std::string kind, arg;
    std::size_t colon = spec.find(':');
    if (colon == std::string::npos) {
        kind = "call";
        arg = spec;
    } else {
        kind = spec.substr(0, colon);
        arg = spec.substr(colon + 1);
    }

    if (kind == "call") {
        if (!probes) return false;
        if (live) return probes->armLive(arg);
        probes->configure(arg); // resolved on module load
        return true;
    }
    if (!triggers) return false;
    if (kind == "alloc") { triggers->add(SnapshotTriggers::Kind::Alloc, arg, "alloc:" + arg); return true; }
    if (kind == "throw") { triggers->add(SnapshotTriggers::Kind::Throw, arg, arg.empty() ? "throw" : "throw:" + arg); return true; }
    if (kind == "gc")    { triggers->add(SnapshotTriggers::Kind::Gc, arg, arg.empty() ? "gc" : "gc:" + arg); return true; }
    return false; // unknown kind
}

void Profiler::fireTrigger(const std::string& display) {
    if (control) {
        control->sendEvent({std::string(control::events::kSnapshotTrigger), display});
    }
}

HRESULT STDMETHODCALLTYPE Profiler::Shutdown() {
    if (isShuttingDown.exchange(true)) {
        return S_OK;
    }
    isInitialized = false;
    if (control) {
        control->stop(); // stop serving requests before we tear down the aggregator
    }
    logger->logInfo("profiler shutting down: " +
                    std::to_string(totalAllocations.load()) + " allocations, " +
                    std::to_string(totalBytes.load()) + " bytes");
    if (aggregator) {
        aggregator->countPendingAsSurvived(); // anything uncollected at exit is still live
        aggregator->dump(outputPath);
    }
    if (trace) {
        trace->stop();
        trace->dump(tracePath, [this](FunctionID f) { return aggregator->resolveMethodName(f); });
    }
    return S_OK;
}

HRESULT STDMETHODCALLTYPE Profiler::ObjectAllocated(ObjectID objectId, ClassID classId) {
    if (!isInitialized.load() || isShuttingDown.load()) {
        return S_OK;
    }

    // alloc: snapshot triggers - fire once when an instance of the armed type is allocated.
    if (triggers && triggers->wantsAlloc()) {
        if (auto display = triggers->onAlloc(aggregator->resolveTypeName(classId)))
            fireTrigger(*display);
    }

    ULONG objectSize = 0;
    corProfilerInfo->GetObjectSize(objectId, &objectSize);

    totalAllocations.fetch_add(1, std::memory_order_relaxed);
    totalBytes.fetch_add(objectSize, std::memory_order_relaxed);

    // Sampling gate: when an interval is set, only every ~N bytes pays for the
    // (expensive) stack walk; 0 means sample every allocation.
    bool take = sampleInterval == 0;
    if (!take) {
        t_bytesSinceSample += objectSize;
        if (t_bytesSinceSample >= sampleInterval) {
            t_bytesSinceSample = 0;
            take = true;
        }
    }
    if (!take)
        return S_OK;

    // Capture the call stack (leaf -> root) and attribute the allocation to it.
    t_frames.clear();
    corProfilerInfo->DoStackSnapshot(0 /* current thread */, captureStack,
                                     COR_PRF_SNAPSHOT_DEFAULT, this, nullptr, 0);

    aggregator->record(t_frames, objectSize, objectId);
    return S_OK;
}

// --- Required ICorProfilerCallback8 stubs --------------------------------------------------------
HRESULT STDMETHODCALLTYPE Profiler::AppDomainCreationStarted(AppDomainID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::AppDomainCreationFinished(AppDomainID, HRESULT) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::AppDomainShutdownStarted(AppDomainID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::AppDomainShutdownFinished(AppDomainID, HRESULT) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::AssemblyLoadStarted(AssemblyID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::AssemblyLoadFinished(AssemblyID, HRESULT) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::AssemblyUnloadStarted(AssemblyID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::AssemblyUnloadFinished(AssemblyID, HRESULT) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ModuleLoadStarted(ModuleID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ModuleLoadFinished(ModuleID moduleId, HRESULT hrStatus) {
    if (probes && SUCCEEDED(hrStatus))
        probes->onModuleLoaded(moduleId);
    return S_OK;
}
HRESULT STDMETHODCALLTYPE Profiler::ModuleUnloadStarted(ModuleID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ModuleUnloadFinished(ModuleID, HRESULT) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ModuleAttachedToAssembly(ModuleID, AssemblyID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ClassLoadStarted(ClassID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ClassLoadFinished(ClassID, HRESULT) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ClassUnloadStarted(ClassID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ClassUnloadFinished(ClassID, HRESULT) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::FunctionUnloadStarted(FunctionID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::JITCompilationStarted(FunctionID, BOOL) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::JITCompilationFinished(FunctionID, HRESULT, BOOL) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::JITCachedFunctionSearchStarted(FunctionID, BOOL*) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::JITCachedFunctionSearchFinished(FunctionID, COR_PRF_JIT_CACHE) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::JITFunctionPitched(FunctionID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::JITInlining(FunctionID, FunctionID, BOOL*) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ThreadCreated(ThreadID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ThreadDestroyed(ThreadID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ThreadAssignedToOSThread(ThreadID, DWORD) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RemotingClientInvocationStarted() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RemotingClientSendingMessage(GUID*, BOOL) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RemotingClientReceivingReply(GUID*, BOOL) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RemotingClientInvocationFinished() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RemotingServerReceivingMessage(GUID*, BOOL) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RemotingServerInvocationStarted() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RemotingServerInvocationReturned() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RemotingServerSendingReply(GUID*, BOOL) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::UnmanagedToManagedTransition(FunctionID, COR_PRF_TRANSITION_REASON) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ManagedToUnmanagedTransition(FunctionID, COR_PRF_TRANSITION_REASON) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RuntimeSuspendStarted(COR_PRF_SUSPEND_REASON) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RuntimeSuspendFinished() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RuntimeSuspendAborted() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RuntimeResumeStarted() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RuntimeResumeFinished() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RuntimeThreadSuspended(ThreadID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RuntimeThreadResumed(ThreadID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::MovedReferences(ULONG, ObjectID[], ObjectID[], ULONG[]) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ObjectsAllocatedByClass(ULONG, ClassID[], ULONG[]) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ObjectReferences(ObjectID, ClassID, ULONG, ObjectID[]) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RootReferences(ULONG, ObjectID[]) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ExceptionThrown(ObjectID thrownObjectId) {
    // throw: snapshot triggers - fire once when a matching exception type is thrown.
    if (triggers && triggers->wantsThrow() && aggregator) {
        ClassID classId = 0;
        if (SUCCEEDED(corProfilerInfo->GetClassFromObject(thrownObjectId, &classId))) {
            if (auto display = triggers->onThrow(aggregator->resolveTypeName(classId)))
                fireTrigger(*display);
        }
    }
    return S_OK;
}
HRESULT STDMETHODCALLTYPE Profiler::ExceptionSearchFunctionEnter(FunctionID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ExceptionSearchFunctionLeave() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ExceptionSearchFilterEnter(FunctionID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ExceptionSearchFilterLeave() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ExceptionSearchCatcherFound(FunctionID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ExceptionOSHandlerEnter(UINT_PTR) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ExceptionOSHandlerLeave(UINT_PTR) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ExceptionUnwindFunctionEnter(FunctionID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ExceptionUnwindFunctionLeave() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ExceptionUnwindFinallyEnter(FunctionID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ExceptionUnwindFinallyLeave() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ExceptionCatcherEnter(FunctionID, ObjectID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ExceptionCatcherLeave() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::COMClassicVTableCreated(ClassID, REFGUID, void*, ULONG) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::COMClassicVTableDestroyed(ClassID, REFGUID, void*) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ExceptionCLRCatcherFound() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ExceptionCLRCatcherExecute() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ThreadNameChanged(ThreadID, ULONG, WCHAR[]) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::GarbageCollectionStarted(int cGenerations, BOOL generationCollected[], COR_PRF_GC_REASON) {
    gcCount.fetch_add(1, std::memory_order_relaxed); // for snapshot drift detection
    if (aggregator) aggregator->beginGc();
    // Remember the highest generation being collected, for gc: triggers.
    maxGenCollected = 0;
    for (int g = 0; g < cGenerations; ++g)
        if (generationCollected[g]) maxGenCollected = g;
    return S_OK;
}
HRESULT STDMETHODCALLTYPE Profiler::SurvivingReferences(ULONG, ObjectID[], ULONG[]) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::GarbageCollectionFinished() {
    if (aggregator) aggregator->endGc();
    // gc: snapshot triggers - fire once after a collection of the armed generation.
    if (triggers && triggers->wantsGc()) {
        if (auto display = triggers->onGc(maxGenCollected))
            fireTrigger(*display);
    }
    return S_OK;
}
HRESULT STDMETHODCALLTYPE Profiler::FinalizeableObjectQueued(DWORD, ObjectID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RootReferences2(ULONG, ObjectID[], COR_PRF_GC_ROOT_KIND[], COR_PRF_GC_ROOT_FLAGS[], UINT_PTR[]) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::HandleCreated(GCHandleID, ObjectID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::HandleDestroyed(GCHandleID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ProfilerAttachComplete() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ProfilerDetachSucceeded() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ReJITCompilationStarted(FunctionID, ReJITID, BOOL) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::GetReJITParameters(ModuleID moduleId, mdMethodDef methodId, ICorProfilerFunctionControl* pFunctionControl) {
    if (probes)
        return probes->getReJITParameters(moduleId, methodId, pFunctionControl);
    return S_OK;
}
HRESULT STDMETHODCALLTYPE Profiler::ReJITCompilationFinished(FunctionID, ReJITID, HRESULT, BOOL) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ReJITError(ModuleID, mdMethodDef methodId, FunctionID, HRESULT hrStatus) {
    char buf[16];
    std::snprintf(buf, sizeof buf, "0x%08x", static_cast<unsigned>(hrStatus));
    logger->logError("ReJIT error for token " + std::to_string(methodId) + ": " + buf);
    return S_OK;
}
HRESULT STDMETHODCALLTYPE Profiler::MovedReferences2(ULONG cRanges, ObjectID oldStarts[], ObjectID newStarts[], SIZE_T lengths[]) {
    // Compacting survivors: record by OLD address (what pending objects are keyed on),
    // and carry the old->new delta so correlation can follow the object's identity.
    if (aggregator)
        for (ULONG i = 0; i < cRanges; ++i)
            aggregator->noteMove(oldStarts[i], newStarts[i], lengths[i]);
    return S_OK;
}
HRESULT STDMETHODCALLTYPE Profiler::SurvivingReferences2(ULONG cRanges, ObjectID starts[], SIZE_T lengths[]) {
    // Non-compacting survivors: alive in place.
    if (aggregator)
        for (ULONG i = 0; i < cRanges; ++i)
            aggregator->noteSurvivorRange(starts[i], lengths[i]);
    return S_OK;
}
HRESULT STDMETHODCALLTYPE Profiler::ConditionalWeakTableElementReferences(ULONG, ObjectID[], ObjectID[], GCHandleID[]) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::GetAssemblyReferences(const WCHAR*, ICorProfilerAssemblyReferenceProvider*) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ModuleInMemorySymbolsUpdated(ModuleID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::DynamicMethodJITCompilationStarted(FunctionID, BOOL, LPCBYTE, ULONG) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::DynamicMethodJITCompilationFinished(FunctionID, HRESULT, BOOL) { return S_OK; }

} // namespace Sherlock
