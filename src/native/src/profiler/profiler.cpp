#include "sherlock/profiler/profiler.hpp"

#include "sherlock/control/protocol.hpp"
#include "sherlock/profiler/shadowstack.hpp"

#include <chrono>
#include <cctype>
#include <cstdio>
#include <cstdint>
#include <cstdlib>
#include <exception>
#include <filesystem>
#include <format>
#include <span>
#include <string>
#include <thread>
#include <vector>

#ifdef _WIN32
#include <process.h> // _getpid
#else
#include <unistd.h> // getpid
#endif

namespace Sherlock {

namespace {

// Cap stack depth so pathological recursion can't blow up the hot path or the aggregation key.
constexpr std::size_t kMaxFrames = 64;
constexpr std::chrono::seconds kExitCaptureTimeout{90};

// Insert the current pid before the extension (allocations.tsv -> allocations.<pid>.tsv), so every
// profiled process (including children that inherit the env) writes a distinct file.
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

std::string withCaptureId(const std::string& path, std::uint64_t id) {
    const std::string suffix = ".capture-" + std::to_string(id);
    const std::size_t slash = path.find_last_of("/\\");
    const std::size_t dot = path.find_last_of('.');
    if (dot == std::string::npos || (slash != std::string::npos && dot < slash)) {
        return path + suffix;
    }
    return path.substr(0, dot) + suffix + path.substr(dot);
}

std::optional<std::filesystem::path> modulePath(
    ICorProfilerInfo10* info,
    ModuleID moduleId) {
    LPCBYTE base = nullptr;
    ULONG length = 0;
    AssemblyID assembly = 0;
    DWORD flags = 0;
    if (FAILED(info->GetModuleInfo2(
            moduleId, &base, 0, &length, nullptr, &assembly, &flags)) ||
        length == 0 || (flags & COR_PRF_MODULE_DISK) == 0) {
        return std::nullopt;
    }

    std::vector<WCHAR> name(length);
    if (FAILED(info->GetModuleInfo2(
            moduleId, &base, static_cast<ULONG>(name.size()), &length,
            name.data(), &assembly, &flags)) ||
        name.empty() || name[0] == 0) {
        return std::nullopt;
    }
    return std::filesystem::path(std::basic_string<WCHAR>(name.data()));
}

bool equalsIgnoreCase(std::string_view left, std::string_view right) {
    if (left.size() != right.size()) {
        return false;
    }
    for (std::size_t i = 0; i < left.size(); ++i) {
        if (std::tolower(static_cast<unsigned char>(left[i])) !=
            std::tolower(static_cast<unsigned char>(right[i]))) {
            return false;
        }
    }
    return true;
}

// Bytes allocated on this thread since the last sample was taken.
thread_local std::uint64_t t_bytesSinceSample = 0;

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
        logger->error("QueryInterface for ICorProfilerInfo10 failed");
        return hr;
    }

    const char* triggerEnv = std::getenv("SHERLOCK_SNAPSHOT_ON");
    bool hasStartupTriggers = triggerEnv != nullptr && triggerEnv[0] != '\0';
    const char* ctlSocketEnv = std::getenv("SHERLOCK_CONTROL_SOCKET");
    bool controlPresent = ctlSocketEnv != nullptr && ctlSocketEnv[0] != '\0';
    // Snapshot triggers are possible if pre-armed at startup, or if the control channel lets the
    // REPL arm them live.
    bool triggersEnabled = hasStartupTriggers || controlPresent;

    // Allocation tracking (ObjectAllocated) and GC callbacks are always on. The shadow stack (per-thread
    // call stack maintained by ReJIT-injected IL, read O(1) in ObjectAllocated) captures allocation
    // stacks, so ReJIT + module loads are always enabled too.
    DWORD eventMask = COR_PRF_MONITOR_OBJECT_ALLOCATED |
                      COR_PRF_ENABLE_OBJECT_ALLOCATED |
                      COR_PRF_MONITOR_GC | // GC callbacks for survivor tracking + gc: triggers
                      COR_PRF_ENABLE_REJIT | COR_PRF_MONITOR_MODULE_LOADS;
    if (triggersEnabled)
        // Exceptions for throw: triggers. call: triggers simply don't fire on inlined/tiny methods.
        eventMask |= COR_PRF_MONITOR_EXCEPTIONS;

    hr = corProfilerInfo->SetEventMask(eventMask);
    if (FAILED(hr)) {
        logger->error("SetEventMask failed");
        return hr;
    }

    const char* out = std::getenv("SHERLOCK_PROFILE_OUT");
    outputPath = withPid((out != nullptr && out[0] != '\0') ? out : "sherlock-allocations.txt");

    const char* sample = std::getenv("SHERLOCK_SAMPLE_BYTES");
    if (sample != nullptr && sample[0] != '\0')
        sampleInterval = std::strtoull(sample, nullptr, 10);

    aggregator = std::make_unique<Aggregator>(corProfilerInfo, logger.get());
    shadowInstr = std::make_unique<ShadowStackInstrumenter>(corProfilerInfo, logger.get());

    const char* correlateEnv = std::getenv("SHERLOCK_CORRELATE");
    correlate = correlateEnv != nullptr && correlateEnv[0] != '\0' && correlateEnv[0] != '0';
    if (correlate) {
        aggregator->enableCorrelation();
        const char* corrOut = std::getenv("SHERLOCK_CORRELATE_OUT");
        correlationPath = withPid((corrOut != nullptr && corrOut[0] != '\0') ? corrOut : "sherlock-correlation.txt");
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
                if (equalsIgnoreCase(one, "exit")) {
                    snapshotOnExit_ = true;
                } else if (!one.empty()) {
                    armTrigger(one, false);
                }
            }
            logger->trace("snapshot-on: {}", triggerEnv);
        }
    }

    // Control channel: connect to sl if a socket was provided. Carries on-demand requests
    // (emit-correlation, flush-allocations, arm-trigger) and pushes events (snapshot triggers).
    if (controlPresent) {
        control = std::make_unique<control::ControlChannel>(logger.get());
        if (std::optional<std::string> err = control->connect(ctlSocketEnv)) {
            logger->error("control channel connect failed: {}", *err);
            control.reset();
        } else {
            std::vector<std::string> features = {"allocations"};
            if (correlate) features.push_back("correlate");
            if (correlate) features.push_back("coherent-capture");
            if (triggersEnabled) features.push_back("snapshot-triggers");
            if (snapshotOnExit_) features.push_back("exit-capture");
            control->start("0.1", features,
                           [this](std::string_view cmd, std::span<const std::string_view> args) {
                               try {
                                   return handleControl(cmd, args);
                               } catch (const std::exception& ex) {
                                   logger->error("control request failed: {}", ex.what());
                                   return control::Reply::error(ex.what());
                               } catch (...) {
                                   logger->error("control request failed");
                                   return control::Reply::error("internal profiler error");
                               }
                           },
                           [this] {
                               coherentCapture_.forceRelease();
                               exitCapture_.forceRelease();
                           });
            if (probes) {
                probes->setHitCallback([this](const std::string& name, ProbePhase phase) {
                    if (phase == ProbePhase::Return && snapshotOnExit_) {
                        handleEntryPointReturn();
                        return;
                    }
                    fireTrigger((phase == ProbePhase::Enter ? "call:" : "call-exit:") + name);
                });
            }
            logger->trace("control channel connected");
        }
    }

    isInitialized = true;

    logger->trace("profiler initialized; aggregating allocations by call stack");
    return S_OK;
}

HRESULT STDMETHODCALLTYPE Profiler::InitializeForAttach(IUnknown*, void*, UINT) {
    // Attach is unsupported: allocation tracking needs COR_PRF_MONITOR_OBJECT_ALLOCATED, an IMMUTABLE
    // flag SetEventMask rejects on attach (CORPROF_E_IMMUTABLE_FLAGS_SET), so there's no useful degraded
    // mode. Fail clearly at load time. Sherlock attaches at process start via CORECLR_PROFILER.
    if (logger)
        logger->error("Sherlock must be set at startup (CORECLR_PROFILER); runtime attach is not supported.");
    return E_NOTIMPL;
}

control::Reply Profiler::handleControl(std::string_view cmd, std::span<const std::string_view> args) {
    if (cmd == control::commands::kPing) {
        return control::Reply::success("pong");
    }
    if (cmd == control::commands::kEmitCorrelation) {
        if (!correlate || !aggregator) {
            return control::Reply::error("correlation not enabled for this run");
        }
        if (corProfilerInfo == nullptr) {
            return control::Reply::error("no profiler info");
        }
        HRESULT gc = corProfilerInfo->ForceGC(); // settle addresses before emitting
        if (FAILED(gc)) {
            return control::Reply::error(std::format("ForceGC failed: 0x{:08x}", static_cast<unsigned>(gc)));
        }
        const std::string path = withCaptureId(
            correlationPath, snapshotSequence.fetch_add(1, std::memory_order_relaxed));
        if (!aggregator->emitCorrelation(path)) {
            return control::Reply::error("could not write correlation snapshot");
        }
        // Return the GC count at emit; sl re-checks after the dump to detect drift (a GC between emit
        // and dump would move objects and invalidate the address join).
        return control::Reply::success(path + "\t" + std::to_string(gcCount.load()));
    }
    if (cmd == control::commands::kGcCount) {
        return control::Reply::success(std::to_string(gcCount.load()));
    }
    if (cmd == control::commands::kHeapSize) {
        // Live managed-heap size, from the current generation bounds (sum of each gen's live range).
        // Reply is tab-separated: total \t gen0 \t gen1 \t gen2 \t loh \t poh (bytes).
        if (corProfilerInfo == nullptr) {
            return control::Reply::error("no profiler info");
        }
        ULONG count = 0;
        if (FAILED(corProfilerInfo->GetGenerationBounds(0, &count, nullptr)) || count == 0) {
            return control::Reply::error("generation bounds unavailable");
        }
        std::vector<COR_PRF_GC_GENERATION_RANGE> ranges(count);
        if (FAILED(corProfilerInfo->GetGenerationBounds(count, &count, ranges.data()))) {
            return control::Reply::error("generation bounds query failed");
        }
        std::uint64_t gen[5] = {0, 0, 0, 0, 0};
        std::uint64_t total = 0;
        for (ULONG i = 0; i < count; ++i) {
            const auto len = static_cast<std::uint64_t>(ranges[i].rangeLength);
            const int g = static_cast<int>(ranges[i].generation);
            if (g >= 0 && g <= 4) {
                gen[g] += len;
            }
            total += len;
        }
        return control::Reply::success(
            std::to_string(total) + "\t" + std::to_string(gen[0]) + "\t" + std::to_string(gen[1]) + "\t" +
            std::to_string(gen[2]) + "\t" + std::to_string(gen[3]) + "\t" + std::to_string(gen[4]));
    }
    if (cmd == control::commands::kFlushAllocations) {
        if (!aggregator) {
            return control::Reply::error("no aggregator");
        }
        const std::string path = withCaptureId(
            outputPath, snapshotSequence.fetch_add(1, std::memory_order_relaxed));
        return aggregator->dump(path)
            ? control::Reply::success(path)
            : control::Reply::error("could not write allocation snapshot");
    }
    if (cmd == control::commands::kArmTrigger) {
        if (args.empty()) {
            return control::Reply::error("arm-trigger needs a <kind:arg> spec");
        }
        return armTrigger(std::string(args[0]), /*live=*/true)
            ? control::Reply::success("armed")
            : control::Reply::error("could not arm (unknown kind, or method not loaded yet)");
    }
    if (cmd == control::commands::kBeginCoherentCapture) {
        if (args.empty() || args[0].empty()) {
            return control::Reply::error("begin-coherent-capture needs a <token>");
        }
        if (!correlate || !aggregator) {
            return control::Reply::error("coherent capture needs SHERLOCK_CORRELATE");
        }
        if (corProfilerInfo == nullptr) {
            return control::Reply::error("no profiler info");
        }
        std::string token(args[0]);
        if (coherentCapture_.active()) {
            return control::Reply::error("a coherent capture is already in progress");
        }
        if (coherentForceGcRunning_.load(std::memory_order_acquire)) {
            return control::Reply::error("the previous coherent capture GC is still finishing");
        }
        if (coherentForceGcThread_.joinable()) {
            coherentForceGcThread_.join();
        }
        if (!coherentCapture_.begin(token)) {
            return control::Reply::error("a coherent capture is already in progress");
        }
        // Runs on its own native thread: ForceGC cannot be called with a profiler callback on the
        // stack, and calling it inline here would block this control (reader) thread until release
        // - but complete-coherent-capture/abort-coherent-capture can only arrive on that same
        // reader thread, so it would never be able to release itself. Returns immediately.
        coherentForceGcRunning_.store(true, std::memory_order_release);
        try {
            coherentForceGcThread_ = std::thread(&Profiler::runCoherentForceGc, this, token);
        } catch (...) {
            coherentForceGcRunning_.store(false, std::memory_order_release);
            (void)coherentCapture_.abort(token);
            throw;
        }
        return control::Reply::success("armed");
    }
    if (cmd == control::commands::kCompleteCoherentCapture || cmd == control::commands::kAbortCoherentCapture) {
        if (args.empty() || args[0].empty()) {
            return control::Reply::error("token required");
        }
        const bool complete = (cmd == control::commands::kCompleteCoherentCapture);
        const std::string token(args[0]);

        if (!complete) {
            if (!coherentCapture_.abort(token)) {
                return control::Reply::error("no coherent capture is active for this token");
            }
            return control::Reply::success("aborted");
        }

        std::string path;
        if (!coherentCapture_.isParkedFor(token)) {
            return control::Reply::error("no coherent capture is parked for this token");
        }
        // The GC callback is parked past endGc(), so no extra ForceGC is needed.
        path = withCaptureId(correlationPath, snapshotSequence.fetch_add(1, std::memory_order_relaxed));
        if (!aggregator->emitCorrelation(path)) {
            (void)coherentCapture_.abort(token);
            if (coherentForceGcThread_.joinable()) {
                coherentForceGcThread_.join();
            }
            return control::Reply::error("could not write coherent capture snapshot");
        }

        std::uint64_t gcCountAtReady = 0;
        if (!coherentCapture_.release(token, gcCountAtReady)) {
            std::remove(path.c_str());
            if (coherentForceGcThread_.joinable()) {
                coherentForceGcThread_.join();
            }
            return control::Reply::error("no coherent capture is parked for this token");
        }
        if (coherentForceGcThread_.joinable()) {
            coherentForceGcThread_.join();
        }
        return control::Reply::success(path + "\t" + std::to_string(gcCountAtReady));
    }
    if (cmd == control::commands::kReleaseExitCapture) {
        if (args.empty() || args[0].empty()) {
            return control::Reply::error("release-exit-capture needs a <token>");
        }
        return exitCapture_.release(std::string(args[0]))
            ? control::Reply::success("released")
            : control::Reply::error("no exit capture is waiting for this token");
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

void Profiler::fireTrigger(const std::string& display) noexcept {
    try {
        if (control) {
            (void)control->sendEvent({std::string(control::events::kSnapshotTrigger), display});
        }
    } catch (const std::exception& ex) {
        logger->error("could not send snapshot trigger: {}", ex.what());
    } catch (...) {
        logger->error("could not send snapshot trigger");
    }
}

bool Profiler::armExitEntryPoint(ModuleID moduleId) {
    if (!snapshotOnExit_ || exitEntryPointArmed_.load(std::memory_order_acquire) || !probes) {
        return false;
    }
    std::optional<std::filesystem::path> path = modulePath(corProfilerInfo, moduleId);
    if (!path) {
        return false;
    }
    std::optional<std::uint32_t> token = entrypoint::fromFile(*path);
    if (!token) {
        return false;
    }

    IMetaDataImport* metadata = nullptr;
    if (FAILED(corProfilerInfo->GetModuleMetaData(
            moduleId, ofRead, IID_IMetaDataImport,
            reinterpret_cast<IUnknown**>(&metadata))) ||
        metadata == nullptr) {
        return false;
    }
    const bool valid = SUCCEEDED(metadata->GetMethodProps(
        static_cast<mdMethodDef>(*token), nullptr, nullptr, 0, nullptr,
        nullptr, nullptr, nullptr, nullptr, nullptr));
    metadata->Release();
    if (!valid) {
        return false;
    }

    bool expected = false;
    if (!exitEntryPointArmed_.compare_exchange_strong(
            expected, true, std::memory_order_acq_rel)) {
        return false;
    }
    try {
        probes->registerMethod(
            moduleId, static_cast<mdMethodDef>(*token), "entrypoint", ProbeEvents::Return);
    } catch (...) {
        exitEntryPointArmed_.store(false, std::memory_order_release);
        throw;
    }
    logger->trace("snapshot-on exit armed for entry-point token 0x{:08x}", *token);
    return true;
}

void Profiler::handleEntryPointReturn() noexcept {
    exitCaptureFired_.store(true, std::memory_order_release);
    const std::string token =
        std::to_string(exitCaptureSequence_.fetch_add(1, std::memory_order_relaxed));
    if (!exitCapture_.begin(token)) {
        return;
    }

    bool sent = false;
    try {
        sent = control && control->connected() && control->sendEvent({
            std::string(control::events::kExitCaptureReady), token});
    } catch (const std::exception& ex) {
        logger->error("snapshot-on exit: could not send ready event: {}", ex.what());
    } catch (...) {
        logger->error("snapshot-on exit: could not send ready event");
    }
    if (!sent) {
        logger->error("snapshot-on exit: supervisor unavailable; allowing Main to return");
        exitCapture_.forceRelease();
    }

    if (exitCapture_.wait(kExitCaptureTimeout) ==
        control::ExitCaptureLatch::WaitResult::TimedOut) {
        logger->error("snapshot-on exit: timed out after 90s; allowing Main to return");
    }
}

// EXPERIMENTAL "coherent capture". Runs on its own native thread (never the control reader thread
// or a profiler-callback thread - ForceGC forbids the latter). ForceGC blocks until the induced GC
// (and, if it's the armed one, the park in handleCoherentCaptureGc) fully completes, so this thread
// stays alive for the whole barrier lifetime.
void Profiler::runCoherentForceGc(std::string token) noexcept {
    try {
        HRESULT hr = corProfilerInfo->ForceGC();
        if (FAILED(hr)) {
            if (coherentCapture_.abort(token) && control) {
                (void)control->sendEvent({
                    std::string(control::events::kCoherentCaptureFailed),
                    token,
                    std::format("ForceGC failed: 0x{:08x}", static_cast<unsigned>(hr))});
            }
        }
        // On success the armed GC's GarbageCollectionFinished has already parked and been released
        // by the time ForceGC() returns; nothing left to do here.
    } catch (const std::exception& ex) {
        (void)coherentCapture_.abort(token);
        logger->error("coherent capture: ForceGC thread failed: {}", ex.what());
    } catch (...) {
        (void)coherentCapture_.abort(token);
        logger->error("coherent capture: ForceGC thread failed");
    }
    coherentForceGcRunning_.store(false, std::memory_order_release);
}

// Called from GarbageCollectionFinished, after aggregator->endGc() has remapped the live set, only
// when coherentCapture_.active() is true (Arming or Parked somewhere). Must not throw across the
// callback boundary.
void Profiler::handleCoherentCaptureGc() noexcept {
    try {
        const std::uint64_t gc = gcCount.load(std::memory_order_relaxed);
        if (!coherentCapture_.markReady(gc)) {
            return; // not the armed GC (barrier idle, or a foreign GC raced in)
        }
        const std::string armedToken = coherentCapture_.token(); // still set: park() clears it on exit
        bool readySent = false;
        try {
            readySent = control && control->sendEvent({
                std::string(control::events::kCoherentCaptureReady),
                armedToken,
                std::to_string(gc)});
        } catch (const std::exception& ex) {
            logger->error("coherent capture: could not send ready event: {}", ex.what());
        } catch (...) {
            logger->error("coherent capture: could not send ready event");
        }
        if (!readySent) {
            logger->error("coherent capture: could not send ready event; releasing GC barrier");
            coherentCapture_.forceRelease();
        }
        // Blocks this GC callback - and so the CLR, which stays GC-stalled - until complete/abort
        // releases it or the hard timeout below fires. The timeout always releases: it's a safety
        // net against a crashed or hung sl leaving the target process wedged forever.
        control::CoherentCaptureBarrier::ParkResult result =
            coherentCapture_.park(std::chrono::seconds(60));
        if (result == control::CoherentCaptureBarrier::ParkResult::TimedOut) {
            logger->error("coherent capture: 60s timeout waiting for complete/abort; releasing GC barrier (token {})", armedToken);
        }
    } catch (const std::exception& ex) {
        logger->error("coherent capture: GC callback failed: {}", ex.what());
    } catch (...) {
        logger->error("coherent capture: GC callback failed");
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
    // Only safe to touch coherentForceGcThread_/coherentCapture_ here because control->stop() just
    // above has already joined the reader thread, so no in-flight begin/complete/abort call can be
    // racing this. Wake a barrier that's Arming or Parked (bounded by its own 60s hard timeout if
    // it's genuinely stuck) and join, before the aggregator it may still be reading gets torn down.
    coherentCapture_.forceRelease();
    exitCapture_.forceRelease();
    if (coherentForceGcThread_.joinable()) {
        coherentForceGcThread_.join();
    }
    logger->trace(
        "profiler shutting down: {} allocations, {} bytes",
        totalAllocations.load(), totalBytes.load());
    if (shadowInstr) {
        logger->trace(
            "shadow-stack instrumentation: {} methods instrumented, {} skipped",
            shadowInstr->instrumentedCount(), shadowInstr->skippedCount());
    }
    if (aggregator) {
        aggregator->countPendingAsSurvived(); // anything uncollected at exit is still live
        if (!aggregator->dump(outputPath)) {
            logger->error("could not write final allocation profile");
        }
    }
    if (snapshotOnExit_ && !exitEntryPointArmed_.load(std::memory_order_acquire)) {
        logger->warn("snapshot-on exit: no managed entry point was found");
    } else if (snapshotOnExit_ && !exitCaptureFired_.load(std::memory_order_acquire)) {
        logger->warn("snapshot-on exit: no normal entry-point return was observed");
    }
    return S_OK;
}

HRESULT STDMETHODCALLTYPE Profiler::ObjectAllocated(ObjectID objectId, ClassID classId) {
    if (!isInitialized.load() || isShuttingDown.load()) {
        return S_OK;
    }
    // The body allocates (record/resolveTypeName/std::string) and must never let an exception escape
    // into the CLR across the COM boundary (UB). Contain it and always return S_OK.
    try {
        // Object size drives totals, the sampling gate, and the aggregator record. GetObjectSize2
        // returns SIZE_T (64-bit) so it doesn't truncate >4 GB LOH objects; skip on failure rather
        // than record a bogus 0.
        SIZE_T objectSize = 0;
        if (FAILED(corProfilerInfo->GetObjectSize2(objectId, &objectSize)) || objectSize == 0) {
            return S_OK;
        }

        totalAllocations.fetch_add(1, std::memory_order_relaxed);
        totalBytes.fetch_add(objectSize, std::memory_order_relaxed);

        // alloc: triggers, fire once when an instance of the armed type is allocated. resolveTypeName
        // is a (cached) metadata lookup, so only pay it when an alloc trigger is actually armed.
        if (triggers && triggers->wantsAlloc()) {
            if (auto display = triggers->onAlloc(aggregator->resolveTypeName(classId)))
                fireTrigger(*display);
        }

        // Sampling gate: when an interval is set, only every ~N bytes pays for the (expensive)
        // stack walk; 0 means sample every allocation.
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

        // Attribute the allocation to the shadow stack. It's stored root->leaf; we hand record() a
        // span directly into that storage (no copy, no stack walk). When deeper than kMaxFrames, keep
        // the leaf-most frames (innermost callers nearest the allocation), the contiguous tail.
        const std::uint32_t depth = shadow::storedDepth();
        const std::uint32_t n = depth < kMaxFrames ? depth : static_cast<std::uint32_t>(kMaxFrames);
        const FunctionID* sf = shadow::frames();
        std::span<const FunctionID> frames;
        if (n > 0) {
            frames = {sf + (depth - n), n};
        }
        aggregator->record(frames, objectSize, objectId, classId);
    } catch (...) {
        // Swallow: a throw crossing back into the runtime would be undefined behavior.
    }
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
    if (FAILED(hrStatus)) {
        return S_OK;
    }
    try {
        if (snapshotOnExit_) {
            (void)armExitEntryPoint(moduleId);
        }
        // Resolve probes first. The global shadow-stack ReJIT that follows then sees and
        // composes those plans without issuing a duplicate startup ReJIT request.
        if (probes) {
            probes->onModuleLoaded(moduleId);
        }
        if (shadowInstr) {
            shadowInstr->onModuleLoaded(moduleId);
        }
    } catch (const std::exception& ex) {
        logger->error("module instrumentation failed: {}", ex.what());
    } catch (...) {
        logger->error("module instrumentation failed");
    }
    return S_OK;
}
HRESULT STDMETHODCALLTYPE Profiler::ModuleUnloadStarted(ModuleID) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ModuleUnloadFinished(ModuleID moduleId, HRESULT hrStatus) {
    if (SUCCEEDED(hrStatus)) {
        if (probes) {
            probes->onModuleUnloaded(moduleId);
        }
        if (shadowInstr) {
            shadowInstr->onModuleUnloaded(moduleId);
        }
    }
    return S_OK;
}
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
HRESULT STDMETHODCALLTYPE Profiler::JITInlining(FunctionID, FunctionID, BOOL* shouldInline) {
    if (shouldInline) {
        *shouldInline = TRUE;
    }
    return S_OK;
}
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
    // throw: triggers, fire once when a matching exception type is thrown.
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
    int maxGen = 0;
    for (int g = 0; g < cGenerations; ++g)
        if (generationCollected[g]) maxGen = g;
    maxGenCollected.store(maxGen, std::memory_order_relaxed);

    // Report the address spans of the condemned generation(s) to the aggregator. The GC only reports
    // survivors for generations it collects; a tracked object in a higher, un-collected gen must be
    // carried over, not dropped. GetGenerationBounds here reflects the pre-collection layout.
    if (aggregator && corProfilerInfo) {
        ULONG count = 0;
        if (SUCCEEDED(corProfilerInfo->GetGenerationBounds(0, &count, nullptr)) && count > 0) {
            std::vector<COR_PRF_GC_GENERATION_RANGE> ranges(count);
            if (SUCCEEDED(corProfilerInfo->GetGenerationBounds(count, &count, ranges.data()))) {
                for (ULONG i = 0; i < count; ++i) {
                    const int g = static_cast<int>(ranges[i].generation);
                    if (ranges[i].rangeLength == 0)
                        continue;
                    // A gen-N collection condemns gens 0..N. LOH (gen 3) / POH (gen 4) are collected
                    // with a gen-2 (full) GC, so include them when the condemned gen is 2.
                    const bool isCondemned = (g >= 0 && g <= maxGen) ||
                                             (g >= 3 && maxGen >= 2);
                    if (isCondemned) {
                        aggregator->noteCondemnedRange(
                            ranges[i].rangeStart, static_cast<std::uint64_t>(ranges[i].rangeLength));
                    }
                    // Separately report the LOH/POH spans (gen 3/4) regardless of condemnation: a large
                    // object allocated between full GCs is never survivor-reported by an ephemeral GC,
                    // so the aggregator admits it as alive when it's on the LOH/POH but not condemned.
                    if (g >= 3) {
                        aggregator->noteLargeObjectRange(
                            ranges[i].rangeStart, static_cast<std::uint64_t>(ranges[i].rangeLength));
                    }
                }
            }
        }
    }
    return S_OK;
}
HRESULT STDMETHODCALLTYPE Profiler::SurvivingReferences(ULONG, ObjectID[], ULONG[]) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::GarbageCollectionFinished() {
    if (aggregator) aggregator->endGc();
    // gc: triggers, fire once after a collection of the armed generation.
    if (triggers && triggers->wantsGc()) {
        if (auto display = triggers->onGc(maxGenCollected.load(std::memory_order_relaxed)))
            fireTrigger(*display);
    }
    // EXPERIMENTAL coherent capture: active() is a lock-free check, so this costs nothing when no
    // capture is in flight (the overwhelmingly common case for this opt-in feature). endGc() above
    // has already remapped the live set, which is the precondition for parking here.
    if (coherentCapture_.active()) {
        handleCoherentCaptureGc();
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
    try {
        // There is exactly one body rewrite. Trigger lookup happens once here; the resulting
        // stable cookie is embedded into the method alongside shadow-stack maintenance.
        ProbePlan probe = probes ? probes->planFor(moduleId, methodId) : ProbePlan{};
        if (shadowInstr) {
            const bool rewritten =
                shadowInstr->rewrite(moduleId, methodId, probe, pFunctionControl);
            if (!rewritten && probe) {
                logger->warn(
                    "armed method token {} could not be instrumented; it can be armed again to retry",
                    methodId);
            }
        }
    } catch (const std::exception& ex) {
        logger->error("ReJIT rewrite failed: {}", ex.what());
    } catch (...) {
        logger->error("ReJIT rewrite failed");
    }
    return S_OK;
}
HRESULT STDMETHODCALLTYPE Profiler::ReJITCompilationFinished(FunctionID, ReJITID, HRESULT, BOOL) { return S_OK; }
HRESULT STDMETHODCALLTYPE Profiler::ReJITError(ModuleID, mdMethodDef methodId, FunctionID, HRESULT hrStatus) {
    logger->error(
        "ReJIT error for token {}: 0x{:08x}",
        methodId, static_cast<unsigned>(hrStatus));
    // Feed the circuit breaker: enough rewrite rejections latch the shadow-stack instrumenter off so
    // we stop handing the runtime IL it won't accept.
    if (shadowInstr)
        shadowInstr->noteReJITError();
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
