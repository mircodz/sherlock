#pragma once

#include <atomic>
#include <cstdint>
#include <memory>
#include <string>

// CLR Profiling headers - profilercommon.h handles OS detection automatically
#include "profilercommon.h"
#include "sherlock/common/logger.hpp"
#include "sherlock/control/channel.hpp"
#include "sherlock/profiler/aggregator.hpp"
#include "sherlock/profiler/probe.hpp"
#include "sherlock/profiler/trace.hpp"
#include "sherlock/profiler/triggers.hpp"

namespace Sherlock {

class Profiler : public ICorProfilerCallback8 {
public:
    Profiler();
    virtual ~Profiler();

    // IUnknown
    STDMETHOD_(ULONG, AddRef)(void) override { return ++refCount; }
    STDMETHOD_(ULONG, Release)(void) override;
    STDMETHOD(QueryInterface)(REFIID riid, void** ppInterface) override;

    // ICorProfilerCallback lifecycle
    STDMETHOD(Initialize)(IUnknown* pICorProfilerInfoUnk) override;
    STDMETHOD(InitializeForAttach)(IUnknown* pICorProfilerInfoUnk, void* pvClientData, UINT cbClientData) override;
    STDMETHOD(Shutdown)() override;

    // The one callback we care about
    STDMETHOD(ObjectAllocated)(ObjectID objectId, ClassID classId) override;

    // Everything else: no-op stubs required by the interface.
    STDMETHOD(AppDomainCreationStarted)(AppDomainID appDomainId) override;
    STDMETHOD(AppDomainCreationFinished)(AppDomainID appDomainId, HRESULT hrStatus) override;
    STDMETHOD(AppDomainShutdownStarted)(AppDomainID appDomainId) override;
    STDMETHOD(AppDomainShutdownFinished)(AppDomainID appDomainId, HRESULT hrStatus) override;
    STDMETHOD(AssemblyLoadStarted)(AssemblyID assemblyId) override;
    STDMETHOD(AssemblyLoadFinished)(AssemblyID assemblyId, HRESULT hrStatus) override;
    STDMETHOD(AssemblyUnloadStarted)(AssemblyID assemblyId) override;
    STDMETHOD(AssemblyUnloadFinished)(AssemblyID assemblyId, HRESULT hrStatus) override;
    STDMETHOD(ModuleLoadStarted)(ModuleID moduleId) override;
    STDMETHOD(ModuleLoadFinished)(ModuleID moduleId, HRESULT hrStatus) override;
    STDMETHOD(ModuleUnloadStarted)(ModuleID moduleId) override;
    STDMETHOD(ModuleUnloadFinished)(ModuleID moduleId, HRESULT hrStatus) override;
    STDMETHOD(ModuleAttachedToAssembly)(ModuleID moduleId, AssemblyID AssemblyId) override;
    STDMETHOD(ClassLoadStarted)(ClassID classId) override;
    STDMETHOD(ClassLoadFinished)(ClassID classId, HRESULT hrStatus) override;
    STDMETHOD(ClassUnloadStarted)(ClassID classId) override;
    STDMETHOD(ClassUnloadFinished)(ClassID classId, HRESULT hrStatus) override;
    STDMETHOD(FunctionUnloadStarted)(FunctionID functionId) override;
    STDMETHOD(JITCompilationStarted)(FunctionID functionId, BOOL fIsSafeToBlock) override;
    STDMETHOD(JITCompilationFinished)(FunctionID functionId, HRESULT hrStatus, BOOL fIsSafeToBlock) override;
    STDMETHOD(JITCachedFunctionSearchStarted)(FunctionID functionId, BOOL* pbUseCachedFunction) override;
    STDMETHOD(JITCachedFunctionSearchFinished)(FunctionID functionId, COR_PRF_JIT_CACHE result) override;
    STDMETHOD(JITFunctionPitched)(FunctionID functionId) override;
    STDMETHOD(JITInlining)(FunctionID callerId, FunctionID calleeId, BOOL* pfShouldInline) override;
    STDMETHOD(ThreadCreated)(ThreadID threadId) override;
    STDMETHOD(ThreadDestroyed)(ThreadID threadId) override;
    STDMETHOD(ThreadAssignedToOSThread)(ThreadID managedThreadId, DWORD osThreadId) override;
    STDMETHOD(RemotingClientInvocationStarted)() override;
    STDMETHOD(RemotingClientSendingMessage)(GUID* pCookie, BOOL fIsAsync) override;
    STDMETHOD(RemotingClientReceivingReply)(GUID* pCookie, BOOL fIsAsync) override;
    STDMETHOD(RemotingClientInvocationFinished)() override;
    STDMETHOD(RemotingServerReceivingMessage)(GUID* pCookie, BOOL fIsAsync) override;
    STDMETHOD(RemotingServerInvocationStarted)() override;
    STDMETHOD(RemotingServerInvocationReturned)() override;
    STDMETHOD(RemotingServerSendingReply)(GUID* pCookie, BOOL fIsAsync) override;
    STDMETHOD(UnmanagedToManagedTransition)(FunctionID functionId, COR_PRF_TRANSITION_REASON reason) override;
    STDMETHOD(ManagedToUnmanagedTransition)(FunctionID functionId, COR_PRF_TRANSITION_REASON reason) override;
    STDMETHOD(RuntimeSuspendStarted)(COR_PRF_SUSPEND_REASON suspendReason) override;
    STDMETHOD(RuntimeSuspendFinished)() override;
    STDMETHOD(RuntimeSuspendAborted)() override;
    STDMETHOD(RuntimeResumeStarted)() override;
    STDMETHOD(RuntimeResumeFinished)() override;
    STDMETHOD(RuntimeThreadSuspended)(ThreadID threadId) override;
    STDMETHOD(RuntimeThreadResumed)(ThreadID threadId) override;
    STDMETHOD(MovedReferences)(ULONG cMovedObjectIDRanges, ObjectID oldObjectIDRangeStart[], ObjectID newObjectIDRangeStart[], ULONG cObjectIDRangeLength[]) override;
    STDMETHOD(ObjectsAllocatedByClass)(ULONG cClassCount, ClassID classIds[], ULONG cObjects[]) override;
    STDMETHOD(ObjectReferences)(ObjectID objectId, ClassID classId, ULONG cObjectRefs, ObjectID objectRefIds[]) override;
    STDMETHOD(RootReferences)(ULONG cRootRefs, ObjectID rootRefIds[]) override;
    STDMETHOD(ExceptionThrown)(ObjectID thrownObjectId) override;
    STDMETHOD(ExceptionSearchFunctionEnter)(FunctionID functionId) override;
    STDMETHOD(ExceptionSearchFunctionLeave)() override;
    STDMETHOD(ExceptionSearchFilterEnter)(FunctionID functionId) override;
    STDMETHOD(ExceptionSearchFilterLeave)() override;
    STDMETHOD(ExceptionSearchCatcherFound)(FunctionID functionId) override;
    STDMETHOD(ExceptionOSHandlerEnter)(UINT_PTR __unused) override;
    STDMETHOD(ExceptionOSHandlerLeave)(UINT_PTR __unused) override;
    STDMETHOD(ExceptionUnwindFunctionEnter)(FunctionID functionId) override;
    STDMETHOD(ExceptionUnwindFunctionLeave)() override;
    STDMETHOD(ExceptionUnwindFinallyEnter)(FunctionID functionId) override;
    STDMETHOD(ExceptionUnwindFinallyLeave)() override;
    STDMETHOD(ExceptionCatcherEnter)(FunctionID functionId, ObjectID objectId) override;
    STDMETHOD(ExceptionCatcherLeave)() override;
    STDMETHOD(COMClassicVTableCreated)(ClassID wrappedClassId, REFGUID implementedIID, void* pVTable, ULONG cSlots) override;
    STDMETHOD(COMClassicVTableDestroyed)(ClassID wrappedClassId, REFGUID implementedIID, void* pVTable) override;
    STDMETHOD(ExceptionCLRCatcherFound)() override;
    STDMETHOD(ExceptionCLRCatcherExecute)() override;
    STDMETHOD(ThreadNameChanged)(ThreadID threadId, ULONG cchName, WCHAR name[]) override;
    STDMETHOD(GarbageCollectionStarted)(int cGenerations, BOOL generationCollected[], COR_PRF_GC_REASON reason) override;
    STDMETHOD(SurvivingReferences)(ULONG cSurvivingObjectIDRanges, ObjectID objectIDRangeStart[], ULONG cObjectIDRangeLength[]) override;
    STDMETHOD(GarbageCollectionFinished)() override;
    STDMETHOD(FinalizeableObjectQueued)(DWORD finalizerFlags, ObjectID objectID) override;
    STDMETHOD(RootReferences2)(ULONG cRootRefs, ObjectID rootRefIds[], COR_PRF_GC_ROOT_KIND rootKinds[], COR_PRF_GC_ROOT_FLAGS rootFlags[], UINT_PTR rootIds[]) override;
    STDMETHOD(HandleCreated)(GCHandleID handleId, ObjectID initialObjectId) override;
    STDMETHOD(HandleDestroyed)(GCHandleID handleId) override;
    STDMETHOD(ProfilerAttachComplete)() override;
    STDMETHOD(ProfilerDetachSucceeded)() override;
    STDMETHOD(ReJITCompilationStarted)(FunctionID functionId, ReJITID rejitId, BOOL fIsSafeToBlock) override;
    STDMETHOD(GetReJITParameters)(ModuleID moduleId, mdMethodDef methodId, ICorProfilerFunctionControl* pFunctionControl) override;
    STDMETHOD(ReJITCompilationFinished)(FunctionID functionId, ReJITID rejitId, HRESULT hrStatus, BOOL fIsSafeToBlock) override;
    STDMETHOD(ReJITError)(ModuleID moduleId, mdMethodDef methodId, FunctionID functionId, HRESULT hrStatus) override;
    STDMETHOD(MovedReferences2)(ULONG cMovedObjectIDRanges, ObjectID oldObjectIDRangeStart[], ObjectID newObjectIDRangeStart[], SIZE_T cObjectIDRangeLength[]) override;
    STDMETHOD(SurvivingReferences2)(ULONG cSurvivingObjectIDRanges, ObjectID objectIDRangeStart[], SIZE_T cObjectIDRangeLength[]) override;
    STDMETHOD(ConditionalWeakTableElementReferences)(ULONG cRootRefs, ObjectID keyRefIds[], ObjectID valueRefIds[], GCHandleID rootIds[]) override;
    STDMETHOD(GetAssemblyReferences)(const WCHAR* wszAssemblyPath, ICorProfilerAssemblyReferenceProvider* pAsmRefProvider) override;
    STDMETHOD(ModuleInMemorySymbolsUpdated)(ModuleID moduleId) override;
    STDMETHOD(DynamicMethodJITCompilationStarted)(FunctionID functionId, BOOL fIsSafeToBlock, LPCBYTE ilHeader, ULONG cbILHeader) override;
    STDMETHOD(DynamicMethodJITCompilationFinished)(FunctionID functionId, HRESULT hrStatus, BOOL fIsSafeToBlock) override;

private:
    std::atomic<ULONG> refCount{0};
    ICorProfilerInfo10* corProfilerInfo = nullptr;
    std::atomic<bool> isInitialized{false};
    std::atomic<bool> isShuttingDown{false};

    std::atomic<std::uint64_t> totalAllocations{0};
    std::atomic<std::uint64_t> totalBytes{0};

    std::string outputPath;
    std::uint64_t sampleInterval = 0; // bytes between samples; 0 = sample every allocation
    std::unique_ptr<Logger> logger;
    std::unique_ptr<Aggregator> aggregator;

    bool traceCalls = false;          // SHERLOCK_TRACE: per-method call tracing via ELT hooks
    std::string tracePath;
    std::unique_ptr<TraceCollector> trace;

    std::unique_ptr<ProbeManager> probes;      // call: triggers via ReJIT
    std::unique_ptr<SnapshotTriggers> triggers; // alloc:/gc:/throw: triggers via callbacks
    int maxGenCollected = 0;                    // set in GarbageCollectionStarted

    bool correlate = false;           // SHERLOCK_CORRELATE: track live objects for snapshot join
    std::string correlationPath;

    // sl <-> profiler control channel (SHERLOCK_CONTROL_SOCKET). Handles on-demand
    // requests (emit-correlation, flush-allocations, arm-trigger) and pushes events.
    std::unique_ptr<control::ControlChannel> control;
    control::Reply handleControl(std::string_view cmd, std::span<const std::string_view> args);

    // Parse & arm one "kind:arg" snapshot trigger (call/alloc/gc/throw). Returns false
    // for an unknown kind or (live call) an unresolved method. `live` = from the REPL.
    bool armTrigger(const std::string& spec, bool live);
    void fireTrigger(const std::string& display); // emit a snapshot-trigger event to sl
};

} // namespace Sherlock
