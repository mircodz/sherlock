#include "sherlock/profiler/shadowstack.hpp"

#include "sherlock/common/logger.hpp"
#include "sherlock/profiler/il_writer.hpp"
#include "sherlock/profiler/probe.hpp"

#include <cstring>
#include <limits>
#include <optional>
#include <vector>

namespace Sherlock {

// ===========================================================================
// Thread-local shadow stack + trampolines
// ===========================================================================
namespace shadow {
thread_local ThreadStack* t_stack = nullptr;

ThreadStack* ensureStack() {
    if (t_stack == nullptr)
        t_stack = new ThreadStack(); // leaked at thread exit; fine for a profiler's lifetime
    return t_stack;
}
} // namespace shadow

extern "C" void Sherlock_ShadowPush(std::int64_t funcId) {
    // Always ++ so push/pop stay balanced even past the cap; only store within bounds.
    shadow::ThreadStack* s = shadow::ensureStack();
    std::uint32_t d = s->depth;
    if (d < shadow::kMaxShadow)
        s->frames[d] = static_cast<FunctionID>(funcId);
    s->depth = d + 1;
}

extern "C" void Sherlock_ShadowPop() {
    shadow::ThreadStack* s = shadow::t_stack;
    if (s != nullptr && s->depth != 0)
        s->depth--;
}

// ===========================================================================
// IL rewriting
// ===========================================================================
namespace {

// Opcode tables, il::Insn, method-header/body decode, and compress/uncompress live in il_writer.{hpp,cpp}.
// The helpers below are shadow-stack-specific (they mint a return-value local + LocalVarSig).
struct RetType {
    bool nonVoid = false;         // false for void (no return-value local needed)
    std::vector<BYTE> blob;       // the RetType signature bytes (only when nonVoid), for the LocalVarSig
};

// Parse the method's return type from its signature. Returns false for return shapes we refuse to
// instrument (byref/typedref, or an exotic RetType we can't size); the caller then skips the method.
// A void return is success with nonVoid=false.
bool parseReturnType(ICorProfilerInfo10* info, ModuleID moduleId, mdMethodDef methodToken, RetType& rt) {
    rt = {};
    IMetaDataImport* md = nullptr;
    if (!SUCCEEDED(info->GetModuleMetaData(moduleId, ofRead, IID_IMetaDataImport, (IUnknown**)&md)) || !md) {
        return false;
    }

    const bool result = [&] {
        PCCOR_SIGNATURE sig = nullptr;
        ULONG sigLen = 0;
        if (FAILED(md->GetMethodProps(
                methodToken, nullptr, nullptr, 0, nullptr, nullptr, &sig, &sigLen, nullptr, nullptr)) ||
            sig == nullptr || sigLen == 0) {
            return false;
        }

        const BYTE* sp = sig; const BYTE* send = sig + sigLen;
        BYTE cc = *sp++;
        std::uint32_t tmp;
        if (cc & IMAGE_CEE_CS_CALLCONV_GENERIC) {
            if (!il::uncompress(sp, send, tmp)) {
                return false;
            }
        }
        if (!il::uncompress(sp, send, tmp)) {
            return false;
        }

        const BYTE* rtStart = sp;
        while (sp < send && (*sp == ELEMENT_TYPE_CMOD_OPT || *sp == ELEMENT_TYPE_CMOD_REQD)) {
            sp++;
            std::uint32_t token;
            if (!il::uncompress(sp, send, token)) {
                return false;
            }
        }
        if (sp >= send) {
            return false;
        }

        BYTE elementType = *sp++;
        if (elementType == ELEMENT_TYPE_VOID) {
            return true;
        }
        if (elementType == ELEMENT_TYPE_BYREF || elementType == ELEMENT_TYPE_TYPEDBYREF) {
            return false;
        }

        bool supported = false;
        switch (elementType) {
            case ELEMENT_TYPE_BOOLEAN: case ELEMENT_TYPE_CHAR:
            case ELEMENT_TYPE_I1: case ELEMENT_TYPE_U1: case ELEMENT_TYPE_I2: case ELEMENT_TYPE_U2:
            case ELEMENT_TYPE_I4: case ELEMENT_TYPE_U4: case ELEMENT_TYPE_I8: case ELEMENT_TYPE_U8:
            case ELEMENT_TYPE_R4: case ELEMENT_TYPE_R8: case ELEMENT_TYPE_I: case ELEMENT_TYPE_U:
            case ELEMENT_TYPE_STRING: case ELEMENT_TYPE_OBJECT:
                supported = true;
                break;
            case ELEMENT_TYPE_CLASS: case ELEMENT_TYPE_VALUETYPE: {
                std::uint32_t token;
                if (!il::uncompress(sp, send, token)) {
                    return false;
                }
                supported = true;
                break;
            }
            default:
                break;
        }
        if (!supported) {
            return false;
        }

        rt.nonVoid = true;
        rt.blob.assign(rtStart, sp);
        return true;
    }();

    md->Release();
    return result;
}

// Build a new LocalVarSig = the method's original locals plus one appended local (the return-value slot),
// emit it, and return its token in `newLocalTok` and the appended local's index in `retLocalIndex`.
// Only called for non-void methods. Returns false if the new signature token can't be minted.
bool buildLocalSig(ICorProfilerInfo10* info, ModuleID moduleId, std::uint32_t localSigTok,
                   const std::vector<BYTE>& retTypeBlob, std::uint32_t& retLocalIndex, mdSignature& newLocalTok) {
    std::uint32_t origLocalCount = 0;
    std::vector<BYTE> origLocalsBody; // bytes after callconv+count
    if (localSigTok != 0) {
        IMetaDataImport* mdi = nullptr;
        if (FAILED(info->GetModuleMetaData(moduleId, ofRead, IID_IMetaDataImport, (IUnknown**)&mdi)) || mdi == nullptr) {
            return false;
        }

        PCCOR_SIGNATURE ls = nullptr;
        ULONG lsLen = 0;
        const bool valid = SUCCEEDED(mdi->GetSigFromToken(
                               static_cast<mdSignature>(localSigTok), &ls, &lsLen)) &&
                           ls != nullptr && lsLen >= 2 &&
                           *ls == IMAGE_CEE_CS_CALLCONV_LOCAL_SIG;
        if (!valid) {
            mdi->Release();
            return false;
        }

        const BYTE* lp = ls + 1;
        const BYTE* lend = ls + lsLen;
        if (!il::uncompress(lp, lend, origLocalCount)) {
            mdi->Release();
            return false;
        }
        origLocalsBody.assign(lp, lend);
        mdi->Release();
    }
    retLocalIndex = origLocalCount;
    std::vector<BYTE> newSig;
    newSig.push_back(0x07); // IMAGE_CEE_CS_CALLCONV_LOCAL_SIG
    il::compress(newSig, origLocalCount + 1);
    newSig.insert(newSig.end(), origLocalsBody.begin(), origLocalsBody.end());
    newSig.insert(newSig.end(), retTypeBlob.begin(), retTypeBlob.end());
    IMetaDataEmit* mde = nullptr;
    if (SUCCEEDED(info->GetModuleMetaData(moduleId, ofRead | ofWrite, IID_IMetaDataEmit, (IUnknown**)&mde)) && mde) {
        HRESULT hr = mde->GetTokenFromSig(newSig.data(), static_cast<ULONG>(newSig.size()), &newLocalTok);
        mde->Release();
        return SUCCEEDED(hr) && newLocalTok != mdSignatureNil;
    }
    return false;
}

void emitProbeCall(il::ILStream& stream, std::uintptr_t cookie, std::uintptr_t target, mdSignature signature) {
    stream.ldc_i8(static_cast<std::uint64_t>(cookie));
    stream.conv_i();
    stream.ldc_i8(static_cast<std::uint64_t>(target));
    stream.conv_i();
    stream.calli(signature);
}

} // namespace

ShadowStackInstrumenter::ShadowStackInstrumenter(ICorProfilerInfo10* info, Logger* logger)
    : info_(info), logger_(logger) {}

ShadowStackInstrumenter::ModuleSigs ShadowStackInstrumenter::ensureSigs(ModuleID moduleId) {
    std::lock_guard lock(sigMutex_);
    auto it = sigByModule_.find(moduleId);
    if (it != sigByModule_.end())
        return it->second;

    ModuleSigs sigs;
    IMetaDataEmit* emit = nullptr;
    if (SUCCEEDED(info_->GetModuleMetaData(moduleId, ofRead | ofWrite, IID_IMetaDataEmit, (IUnknown**)&emit)) && emit != nullptr) {
        // void Sherlock_ShadowPush(int64) — unmanaged C calling convention.
        BYTE pushSig[] = { static_cast<BYTE>(IMAGE_CEE_CS_CALLCONV_C), 0x01,
                           static_cast<BYTE>(ELEMENT_TYPE_VOID), static_cast<BYTE>(ELEMENT_TYPE_I8) };
        if (FAILED(emit->GetTokenFromSig(pushSig, sizeof pushSig, &sigs.push))) {
            sigs.push = mdSignatureNil;
        }
        // void Sherlock_ShadowPop()
        BYTE popSig[] = { static_cast<BYTE>(IMAGE_CEE_CS_CALLCONV_C), 0x00,
                          static_cast<BYTE>(ELEMENT_TYPE_VOID) };
        if (FAILED(emit->GetTokenFromSig(popSig, sizeof popSig, &sigs.pop))) {
            sigs.pop = mdSignatureNil;
        }
        // void Sherlock_Probe*(native int cookie)
        BYTE probeSig[] = { static_cast<BYTE>(IMAGE_CEE_CS_CALLCONV_C), 0x01,
                           static_cast<BYTE>(ELEMENT_TYPE_VOID), static_cast<BYTE>(ELEMENT_TYPE_I) };
        if (FAILED(emit->GetTokenFromSig(probeSig, sizeof probeSig, &sigs.probe))) {
            sigs.probe = mdSignatureNil;
        }
        emit->Release();
    }
    sigByModule_.emplace(moduleId, sigs);
    return sigs;
}

bool ShadowStackInstrumenter::buildIL(FunctionID functionId, ModuleID moduleId,
                                      mdMethodDef methodToken, const ProbePlan& probe,
                                      std::vector<BYTE>& out) {
    ModuleSigs sigs = ensureSigs(moduleId);
    if (sigs.push == mdSignatureNil || sigs.pop == mdSignatureNil) { return false; }
    if (probe && sigs.probe == mdSignatureNil) { return false; }

    LPCBYTE header = nullptr;
    ULONG headerSize = 0;
    if (FAILED(info_->GetILFunctionBody(moduleId, methodToken, &header, &headerSize)) || header == nullptr) {
        return false;
    }

    il::MethodHeader hdr;
    if (!il::parseMethodHeader(header, hdr)) { return false; }
    const BYTE* code = hdr.code;
    const std::uint32_t codeSize = hdr.codeSize;
    const std::uint16_t maxStack = hdr.maxStack;
    const std::uint32_t localSigTok = hdr.localSigTok;
    const bool initLocals = hdr.initLocals;
    const bool moreSects = hdr.moreSects;

    // ---- decode the body ----
    std::vector<il::Insn> insns;
    bool unsafeToWrap = false; // constructs illegal inside a try/finally region
    if (!il::decodeBody(code, codeSize, insns, unsafeToWrap)) { return false; }
    if (insns.empty() || unsafeToWrap) { return false; }

    // ---- return type: extract the RetType blob so we can add a local to stash the returned value
    // before `leave`. Void => no local; exotic/byref return => skip. ----
    RetType rt;
    if (!parseReturnType(info_, moduleId, methodToken, rt)) { return false; }
    const bool nonVoid = rt.nonVoid;
    const std::vector<BYTE>& retTypeBlob = rt.blob;

    // ---- build a new LocalVarSig = original locals + (optional) one retType local ----
    std::uint32_t retLocalIndex = 0;
    mdSignature newLocalTok = static_cast<mdSignature>(localSigTok);
    if (nonVoid && !buildLocalSig(info_, moduleId, localSigTok, retTypeBlob, retLocalIndex, newLocalTok)) {
        return false;
    }

    // ---- layout pass 1: assign new offsets ----
    // Output segments (in order):
    //   [prologue]  shadow push; optional trigger enter
    //   [TRY]       transformed body (ret -> [stloc ret]; leave END)
    //   [FINALLY]   optional trigger exit; shadow pop; endfinally
    //   [END]       (ldloc ret;) ret
    il::ILStream prologue;
    prologue.ldc_i8(static_cast<std::uint64_t>(functionId));
    prologue.ldc_i8(reinterpret_cast<std::uint64_t>(&Sherlock_ShadowPush));
    prologue.conv_i();
    prologue.calli(sigs.push);
    if (probe.onEnter()) {
        emitProbeCall(
            prologue, probe.cookie,
            reinterpret_cast<std::uintptr_t>(&Sherlock_ProbeEnter), sigs.probe);
    }

    // Per-body-instruction new offset map. Each transformed instruction's size is known up front
    // (branches all long-form; ret expands), so we assign offsets directly.
    std::uint32_t tryStart = static_cast<std::uint32_t>(prologue.size());
    std::vector<std::uint32_t> newOff(insns.size());
    auto transformedLen = [&](const il::Insn& in) -> std::uint32_t {
        if (in.ret) {
            std::uint32_t len = 5; // leave <int32>
            if (nonVoid) len += (retLocalIndex <= 0xFF ? 2u : 4u); // stloc
            return len;
        }
        if (in.shortBr) return 5;   // promoted to long
        return in.len;              // long branch / switch / other unchanged size
    };
    std::uint32_t cur = tryStart;
    for (std::size_t i = 0; i < insns.size(); ++i) { newOff[i] = cur; cur += transformedLen(insns[i]); }
    std::uint32_t tryEnd = cur;                 // == handler start
    // finally body
    il::ILStream finallyBody;
    if (probe.onExit()) {
        emitProbeCall(
            finallyBody, probe.cookie,
            reinterpret_cast<std::uintptr_t>(&Sherlock_ProbeExit), sigs.probe);
    }
    finallyBody.ldc_i8(reinterpret_cast<std::uint64_t>(&Sherlock_ShadowPop));
    finallyBody.conv_i();
    finallyBody.calli(sigs.pop);
    finallyBody.endfinally();
    std::uint32_t handlerStart = tryEnd;
    std::uint32_t handlerLen = static_cast<std::uint32_t>(finallyBody.size());
    std::uint32_t endLabel = handlerStart + handlerLen;

    // Map an original body offset -> new offset. Must land on an instruction boundary.
    auto mapOff = [&](std::uint32_t oldOff, std::uint32_t& res) -> bool {
        if (oldOff == codeSize) { res = endLabel; return true; } // branch to end-of-body => END
        for (std::size_t i = 0; i < insns.size(); ++i)
            if (insns[i].off == oldOff) { res = newOff[i]; return true; }
        return false;
    };

    // ---- layout pass 2: emit transformed body bytes ----
    il::ILStream bodyStream;
    std::vector<BYTE>& body = bodyStream.bytes();
    body.reserve(codeSize + insns.size());
    for (std::size_t i = 0; i < insns.size(); ++i) {
        const il::Insn& in = insns[i];
        std::uint32_t here = newOff[i];
        if (in.ret) {
            if (nonVoid) bodyStream.stloc(retLocalIndex);
            std::uint32_t leaveHere = tryStart + (static_cast<std::uint32_t>(body.size())); // absolute of this leave
            std::uint32_t after = leaveHere + 5;
            bodyStream.leave_rel(static_cast<std::int32_t>(endLabel) - static_cast<std::int32_t>(after)); // leave END
            continue;
        }
        if (in.sw) {
            body.push_back(0x45);
            std::uint32_t n = static_cast<std::uint32_t>(in.swTargets.size());
            il::put32(body, n);
            std::uint32_t after = here + 5 + n * 4;
            for (std::uint32_t t : in.swTargets) {
                std::uint32_t tn; if (!mapOff(t, tn)) { return false; }
                il::put32(body, static_cast<std::uint32_t>(static_cast<std::int32_t>(tn) - static_cast<std::int32_t>(after)));
            }
            continue;
        }
        if (in.shortBr || in.longBr) {
            BYTE op = in.shortBr ? il::shortToLong(in.op0) : in.op0;
            body.push_back(op);
            std::uint32_t after = here + 5;
            std::uint32_t tn; if (!mapOff(in.brTarget, tn)) { return false; }
            il::put32(body, static_cast<std::uint32_t>(static_cast<std::int32_t>(tn) - static_cast<std::int32_t>(after)));
            continue;
        }
        // plain instruction: copy raw bytes verbatim
        body.insert(body.end(), in.raw, in.raw + in.len);
    }

    // ---- relocate original EH clauses into the new offset space ----
    // Offsets move non-uniformly (branches widened, ret expanded), unlike the probe's constant shift,
    // so any offset that doesn't land on an instruction boundary means we can't safely rewrite: bail.
    std::vector<il::EHClause> clauses;
    {
        std::vector<il::EHClause> original;
        il::parseEHClauses(header, code, codeSize, moreSects, original);
        // EH region END offsets need a different end-of-body mapping than branch targets: the whole
        // original body lives inside our outer try [tryStart, tryEnd), so an original EH region that
        // ended at codeSize (end of body) must end at tryEnd — NOT endLabel (which is past our own
        // finally). Mapping it to endLabel makes the original handler straddle our try boundary =>
        // illegal non-nested EH => InvalidProgramException. Interior offsets use the normal lookup.
        auto mapEnd = [&](std::uint32_t oldOff, std::uint32_t& res) -> bool {
            if (oldOff == codeSize) { res = tryEnd; return true; }
            return mapOff(oldOff, res);
        };
        auto relocate = [&](il::EHClause e) -> std::optional<il::EHClause> {
            std::uint32_t ts, te, hs, he;
            if (!mapOff(e.tryOffset, ts)) return std::nullopt;
            if (!mapEnd(e.tryOffset + e.tryLength, te)) return std::nullopt;
            if (!mapOff(e.handlerOffset, hs)) return std::nullopt;
            if (!mapEnd(e.handlerOffset + e.handlerLength, he)) return std::nullopt;
            e.tryOffset = ts; e.tryLength = te - ts;
            e.handlerOffset = hs; e.handlerLength = he - hs;
            if (e.flags & il::kClauseFilter) { std::uint32_t f; if (!mapOff(e.classTokenOrFilter, f)) return std::nullopt; e.classTokenOrFilter = f; }
            return e;
        };
        for (const il::EHClause& oc : original) {
            auto e = relocate(oc);
            if (!e) { return false; }
            clauses.push_back(*e);
        }
    }
    // Add our finally clause covering the whole try.
    clauses.push_back(il::EHClause{il::kClauseFinally, tryStart, tryEnd - tryStart, handlerStart, handlerLen, 0});

    // ---- END sequence ----
    il::ILStream endStream;
    if (nonVoid) endStream.ldloc(retLocalIndex);
    endStream.bytes().push_back(0x2A); // ret
    const std::vector<BYTE>& endSeq = endStream.bytes();

    // ---- assemble the full method ----
    const std::vector<BYTE>& prologueBytes = prologue.bytes();
    const std::vector<BYTE>& finallyBytes = finallyBody.bytes();
    const std::uint64_t newCodeSize64 =
        prologueBytes.size() + body.size() + finallyBytes.size() + endSeq.size();
    if (newCodeSize64 > std::numeric_limits<std::uint32_t>::max() ||
        maxStack > std::numeric_limits<std::uint16_t>::max() - 2) {
        return false;
    }
    const std::uint32_t newCodeSize = static_cast<std::uint32_t>(newCodeSize64);

    // Our transform prepends a fixed prologue/finally and grows the body by a few bytes per instruction,
    // so the rewritten code can't legitimately exceed the original plus a bounded margin. A gross overrun
    // means a layout bug produced a malformed body; handing that to the runtime is the in-process hazard
    // we must avoid. Bail (leave the original IL) rather than emit something we can't vouch for.
    constexpr std::uint32_t kFixedOverhead = 256; // prologue + finally + end + slack
    if (newCodeSize > static_cast<std::uint64_t>(codeSize) * 2 + kFixedOverhead) {
        if (logger_)
            logger_->warn(
                "shadow rewrite produced an implausible body size ({} from {}); skipping method",
                newCodeSize, codeSize);
        return false;
    }

    out.clear();
    std::uint16_t newFlags = CorILMethod_FatFormat | CorILMethod_MoreSects;
    if (initLocals) newFlags |= CorILMethod_InitLocals;
    il::put16(out, static_cast<std::uint16_t>((newFlags & 0xFFF) | (3 << 12)));
    il::put16(out, static_cast<std::uint16_t>(maxStack + 2));
    il::put32(out, newCodeSize);
    il::put32(out, static_cast<std::uint32_t>(newLocalTok));
    out.insert(out.end(), prologueBytes.begin(), prologueBytes.end());
    out.insert(out.end(), body.begin(), body.end());
    out.insert(out.end(), finallyBytes.begin(), finallyBytes.end());
    out.insert(out.end(), endSeq.begin(), endSeq.end());

    // EH section (fat), 4-byte aligned.
    while (out.size() & 3) out.push_back(0);
    out.push_back(CorILMethod_Sect_EHTable | CorILMethod_Sect_FatFormat);
    std::uint32_t dataSize = 4 + static_cast<std::uint32_t>(clauses.size()) * 24;
    out.push_back(dataSize & 0xFF); out.push_back((dataSize >> 8) & 0xFF); out.push_back((dataSize >> 16) & 0xFF);
    for (const il::EHClause& e : clauses) {
        il::put32(out, e.flags); il::put32(out, e.tryOffset); il::put32(out, e.tryLength);
        il::put32(out, e.handlerOffset); il::put32(out, e.handlerLength); il::put32(out, e.classTokenOrFilter);
    }

    return true;
}

// --- ReJIT path --------------------------------------------------------------------------
void ShadowStackInstrumenter::onModuleLoaded(ModuleID moduleId) {
    IMetaDataImport* md = nullptr;
    if (FAILED(info_->GetModuleMetaData(moduleId, ofRead, IID_IMetaDataImport, (IUnknown**)&md)) || md == nullptr)
        return;

    // Enumerate every method in this module and request a ReJIT for each. The rewritten IL is supplied
    // later from getReJITParameters (called before first use, and used for inlined bodies too, so we can
    // leave inlining ON). EnumMethods needs a concrete typedef, so walk all type defs (plus the module's
    // <Module> global-methods pseudo-type).
    std::vector<ModuleID> reMods;
    std::vector<mdMethodDef> reToks;

    auto enumTypeMethods = [&](mdTypeDef td) {
        HCORENUM hm = nullptr;
        mdMethodDef toks[256];
        ULONG n = 0;
        while (SUCCEEDED(md->EnumMethods(&hm, td, toks, 256, &n)) && n > 0) {
            for (ULONG i = 0; i < n; ++i) {
                reMods.push_back(moduleId);
                reToks.push_back(toks[i]);
            }
            if (n < 256) break;
        }
        md->CloseEnum(hm);
    };

    // Global methods live under the special token 0x02000001 (<Module>).
    enumTypeMethods(static_cast<mdTypeDef>(0x02000001));

    HCORENUM hTypes = nullptr;
    mdTypeDef types[256];
    ULONG tn = 0;
    while (SUCCEEDED(md->EnumTypeDefs(&hTypes, types, 256, &tn)) && tn > 0) {
        for (ULONG i = 0; i < tn; ++i)
            enumTypeMethods(types[i]);
        if (tn < 256) break;
    }
    md->CloseEnum(hTypes);
    md->Release();

    if (reToks.empty())
        return;
    HRESULT hr = info_->RequestReJIT(static_cast<ULONG>(reToks.size()), reMods.data(), reToks.data());
    if (logger_) {
        logger_->info(
            "shadow ReJIT requested {} method(s), hr=0x{:08x}",
            reToks.size(), static_cast<unsigned>(hr));
    }
}

void ShadowStackInstrumenter::onModuleUnloaded(ModuleID moduleId) {
    std::lock_guard lock(sigMutex_);
    sigByModule_.erase(moduleId);
}

bool ShadowStackInstrumenter::rewrite(ModuleID moduleId, mdMethodDef methodToken,
                                      const ProbePlan& probe,
                                      ICorProfilerFunctionControl* control) {
    // Circuit breaker: once latched off (too many prior ReJITErrors), never rewrite again.
    if (disabled_.load(std::memory_order_relaxed)) {
        skipped_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }

    // Resolve the FunctionID so buildIL can bake it into the push. GetFunctionFromToken gives the
    // canonical (non-generic) FunctionID; generic instantiations share this body's IL, which is fine:
    // the shadow frame just identifies the method, resolved to a name at dump time.
    FunctionID functionId = 0;
    if (FAILED(info_->GetFunctionFromToken(moduleId, methodToken, &functionId)))
        functionId = 0; // still emit; a 0 frame resolves to <unknown> but keeps depth correct

    std::vector<BYTE> out;
    if (!buildIL(functionId, moduleId, methodToken, probe, out)) {
        skipped_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }
    if (FAILED(control->SetILFunctionBody(static_cast<ULONG>(out.size()), out.data()))) {
        skipped_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }
    instrumented_.fetch_add(1, std::memory_order_relaxed);
    return true;
}

void ShadowStackInstrumenter::noteReJITError() {
    // The runtime rejected one of our rewritten bodies. A handful happen on exotic IL we mis-handled;
    // a flood means our rewriting is systematically wrong for this app, so latch off to protect the
    // process. Already-instrumented methods stay instrumented (they compiled fine); we only stop
    // rewriting further ones.
    std::uint64_t n = rejitErrors_.fetch_add(1, std::memory_order_relaxed) + 1;
    if (n >= kMaxReJITErrors && !disabled_.exchange(true, std::memory_order_relaxed)) {
        if (logger_)
            logger_->error(
                "shadow-stack instrumentation disabled after {} ReJIT errors; leaving remaining methods un-instrumented",
                n);
    }
}

} // namespace Sherlock
