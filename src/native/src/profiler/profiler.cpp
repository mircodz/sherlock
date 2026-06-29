// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#include "sherlock/profiler/profiler.hpp"

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

namespace Sherlock {

namespace {

// Cap stack depth so pathological recursion can't blow up the hot path or the
// aggregation key.
constexpr std::size_t kMaxFrames = 64;

// Filled by captureStack during a walk (leaf -> root), read right after. Thread-
// local so concurrent allocating threads don't clobber each other; capacity is
// retained between allocations to avoid reallocation.
thread_local std::vector<FunctionID> t_frames;

// Bytes allocated on this thread since the last sample was taken.
thread_local std::uint64_t t_bytesSinceSample = 0;

// DoStackSnapshot callback: collect managed frames (funcId == 0 marks native /
// runtime frames, which we skip) up to kMaxFrames.
HRESULT __stdcall captureStack(FunctionID funcId, UINT_PTR, COR_PRF_FRAME_INFO,
                               ULONG32, BYTE[], void*) {
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

// ELT2 hooks — the canonical CoreCLR slow-path variant (the runtime saves/restores
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

    // Allocation tracking is always on; tracing adds ELT on top when requested.
    DWORD eventMask = COR_PRF_MONITOR_OBJECT_ALLOCATED |
                      COR_PRF_ENABLE_OBJECT_ALLOCATED |
                      COR_PRF_ENABLE_STACK_SNAPSHOT |
                      COR_PRF_MONITOR_GC; // GC callbacks for survivor tracking
    if (traceCalls)
        eventMask |= COR_PRF_MONITOR_ENTERLEAVE;

    hr = corProfilerInfo->SetEventMask(eventMask);
    if (FAILED(hr)) {
        logger->logError("SetEventMask failed");
        return hr;
    }

    const char* out = std::getenv("SHERLOCK_PROFILE_OUT");
    outputPath = (out != nullptr && out[0] != '\0') ? out : "sherlock-allocations.txt";

    const char* sample = std::getenv("SHERLOCK_SAMPLE_BYTES");
    if (sample != nullptr && sample[0] != '\0')
        sampleInterval = std::strtoull(sample, nullptr, 10);

    aggregator = std::make_unique<Aggregator>(corProfilerInfo, logger.get());

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

    isInitialized = true;
    logger->logInfo(traceCalls
        ? "profiler initialized; allocations by call stack + per-method tracing"
        : "profiler initialized; aggregating allocations by call stack");
    return S_OK;
}

HRESULT STDMETHODCALLTYPE Profiler::InitializeForAttach(IUnknown* pICorProfilerInfoUnk, void*, UINT) {
    return Initialize(pICorProfilerInfoUnk);
}

HRESULT STDMETHODCALLTYPE Profiler::Shutdown() {
    if (isShuttingDown.exchange(true)) {
        return S_OK;
    }
    isInitialized = false;
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
HRESULT STDMETHODCALLTYPE Profiler::ModuleLoadFinished(ModuleID, HRESULT) { return S_OK; }
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
HRESULT STDMETHODCALLTYPE Profiler::ExceptionThrown(ObjectID) { return S_OK; }
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
HRESULT STDMETHODCALLTYPE Profiler::GarbageCollectionStarted(int, BOOL[], COR_PRF_GC_REASON) {
    if (aggregator) aggregator->beginGc();
    return S_OK;
}
HRESULT STDMETHODCALLTYPE Profiler::SurvivingReferences(ULONG, ObjectID[], ULONG[]) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::GarbageCollectionFinished() {
    if (aggregator) aggregator->endGc();
    return S_OK;
}
HRESULT STDMETHODCALLTYPE Profiler::FinalizeableObjectQueued(DWORD, ObjectID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::RootReferences2(ULONG, ObjectID[], COR_PRF_GC_ROOT_KIND[], COR_PRF_GC_ROOT_FLAGS[], UINT_PTR[]) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::HandleCreated(GCHandleID, ObjectID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::HandleDestroyed(GCHandleID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ProfilerAttachComplete() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ProfilerDetachSucceeded() { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ReJITCompilationStarted(FunctionID, ReJITID, BOOL) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::GetReJITParameters(ModuleID, mdMethodDef, ICorProfilerFunctionControl*) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ReJITCompilationFinished(FunctionID, ReJITID, HRESULT, BOOL) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ReJITError(ModuleID, mdMethodDef, FunctionID, HRESULT) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::MovedReferences2(ULONG cRanges, ObjectID oldStarts[], ObjectID[], SIZE_T lengths[]) {
    // Compacting survivors: record them by their OLD address (pre-move), since
    // that's what our pending objects are keyed on.
    if (aggregator)
        for (ULONG i = 0; i < cRanges; ++i)
            aggregator->noteSurvivorRange(oldStarts[i], lengths[i]);
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
