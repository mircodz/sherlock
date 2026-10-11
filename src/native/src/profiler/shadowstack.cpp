#include "sherlock/profiler/shadowstack.hpp"

#include "sherlock/common/logger.hpp"
#include "sherlock/profiler/il_writer.hpp"
#include "sherlock/profiler/probe.hpp"
#include "sherlock/profiler/rejit_policy.hpp"

#include <algorithm>
#include <cstring>
#include <format>
#include <limits>
#include <optional>
#include <vector>

namespace Sherlock {

namespace shadow {
thread_local ThreadStack* t_stack = nullptr;

// Out of memory, a thread goes without a shadow stack rather than retrying on every call.
thread_local bool t_stackFailed = false;

ThreadStack* ensureStack() noexcept {
    if (t_stack == nullptr && !t_stackFailed) {
        try {
            t_stack = new ThreadStack(); // retained for the process lifetime
        } catch (...) {
            t_stackFailed = true; // reached from managed code: no exception may escape
        }
    }
    return t_stack;
}
} // namespace shadow

extern "C" void Sherlock_ShadowPush(std::int64_t frameId) noexcept {
    // Always ++ so push/pop stay balanced past what is stored; store while the buffer has, or can grow, room.
    shadow::ThreadStack* s = shadow::ensureStack();
    if (s == nullptr)
        return;
    std::uint32_t d = s->depth;
    if (d == s->frames.size() && s->growable && d < shadow::kMaxShadow) {
        try {
            s->frames.resize(std::min<std::size_t>(s->frames.size() * 2, shadow::kMaxShadow));
        } catch (...) {
            s->growable = false; // called from managed code: no exception may escape
        }
    }
    if (d < s->frames.size())
        s->frames[d] = static_cast<FrameId>(frameId);
    s->depth = d + 1;
}

extern "C" void Sherlock_ShadowPop() noexcept {
    shadow::ThreadStack* s = shadow::t_stack;
    if (s != nullptr && s->depth != 0)
        s->depth--;
}

namespace {

struct RetType {
    bool nonVoid = false;
    std::vector<BYTE> blob; // non-void RetType bytes for the return-value local
};

// Any return type a local can hold is supported; byref and typedref returns are not. Void needs no return-value local.
// Returns why the method can't be rewritten, or null.
const char* parseReturnType(ICorProfilerInfo10* info, ModuleID moduleId, mdMethodDef methodToken, RetType& rt) {
    rt = {};
    IMetaDataImport* md = nullptr;
    if (!SUCCEEDED(info->GetModuleMetaData(moduleId, ofRead, IID_IMetaDataImport, (IUnknown**)&md)) || !md) {
        return "module metadata is unavailable";
    }

    const char* result = [&]() -> const char* {
        constexpr const char* kMalformed = "its signature can't be read";
        PCCOR_SIGNATURE sig = nullptr;
        ULONG sigLen = 0;
        if (FAILED(md->GetMethodProps(
                methodToken, nullptr, nullptr, 0, nullptr, nullptr, &sig, &sigLen, nullptr, nullptr)) ||
            sig == nullptr || sigLen == 0) {
            return kMalformed;
        }

        const BYTE* sp = sig; const BYTE* send = sig + sigLen;
        BYTE cc = *sp++;
        std::uint32_t tmp;
        if (cc & IMAGE_CEE_CS_CALLCONV_GENERIC) {
            if (!il::uncompress(sp, send, tmp)) {
                return kMalformed;
            }
        }
        if (!il::uncompress(sp, send, tmp)) {
            return kMalformed;
        }

        const BYTE* rtStart = sp;
        const BYTE* type = sp;
        std::uint32_t token;
        while (type < send && (*type == ELEMENT_TYPE_CMOD_OPT || *type == ELEMENT_TYPE_CMOD_REQD)) {
            ++type;
            if (!il::uncompress(type, send, token)) {
                return kMalformed;
            }
        }
        if (type >= send) {
            return kMalformed;
        }
        if (*type == ELEMENT_TYPE_BYREF) {
            return "it returns by reference";
        }
        if (*type == ELEMENT_TYPE_TYPEDBYREF) {
            return "it returns a TypedReference";
        }
        if (*type == ELEMENT_TYPE_VOID) {
            return nullptr;
        }
        if (!il::skipType(sp, send)) {
            return kMalformed;
        }

        rt.nonVoid = true;
        rt.blob.assign(rtStart, sp);
        return nullptr;
    }();

    md->Release();
    return result;
}

// Append a return-value local for a non-void method; return its index and new signature token.
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

ShadowStackInstrumenter::ShadowStackInstrumenter(ICorProfilerInfo10* info, Logger* logger, MethodRegistry& methods)
    : info_(info), logger_(logger), methods_(methods) {}

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

const char* ShadowStackInstrumenter::buildIL(FrameId frameId, ModuleID moduleId,
                                             mdMethodDef methodToken, const ProbePlan& probe,
                                             std::vector<BYTE>& out) {
    ModuleSigs sigs = ensureSigs(moduleId);
    if (sigs.push == mdSignatureNil || sigs.pop == mdSignatureNil) { return "its module can't take the shadow-stack calls"; }
    if (probe && sigs.probe == mdSignatureNil) { return "its module can't take the probe calls"; }

    LPCBYTE header = nullptr;
    ULONG headerSize = 0;
    if (FAILED(info_->GetILFunctionBody(moduleId, methodToken, &header, &headerSize)) || header == nullptr) {
        return "it has no IL body";
    }

    il::MethodHeader hdr;
    if (!il::parseMethodHeader(header, hdr)) { return "its IL header has an unknown format"; }
    const BYTE* code = hdr.code;
    const std::uint32_t codeSize = hdr.codeSize;
    const std::uint16_t maxStack = hdr.maxStack;
    const std::uint32_t localSigTok = hdr.localSigTok;
    const bool initLocals = hdr.initLocals;
    const bool moreSects = hdr.moreSects;

    std::vector<il::Insn> insns;
    bool unsafeToWrap = false; // constructs illegal inside a try/finally region
    if (!il::decodeBody(code, codeSize, insns, unsafeToWrap)) { return "its IL can't be decoded"; }
    if (insns.empty()) { return "its IL body is empty"; }
    if (unsafeToWrap) { return "it uses tail., localloc, or jmp, which can't run inside try/finally"; }

    // leave clears the evaluation stack, so preserve non-void returns in a local.
    RetType rt;
    if (const char* reason = parseReturnType(info_, moduleId, methodToken, rt)) { return reason; }
    const bool nonVoid = rt.nonVoid;
    const std::vector<BYTE>& retTypeBlob = rt.blob;

    std::uint32_t retLocalIndex = 0;
    mdSignature newLocalTok = static_cast<mdSignature>(localSigTok);
    if (nonVoid && !buildLocalSig(info_, moduleId, localSigTok, retTypeBlob, retLocalIndex, newLocalTok)) {
        return "its locals can't take the return value";
    }

    // Layout:
    //   [prologue]  shadow push; optional trigger enter
    //   [TRY]       transformed body (ret -> [stloc ret]; leave END)
    //   [FINALLY]   optional trigger exit; shadow pop; endfinally
    //   [END]       optional normal-return hook; (ldloc ret;) ret
    il::ILStream prologue;
    prologue.ldc_i8(frameId);
    prologue.ldc_i8(reinterpret_cast<std::uint64_t>(&Sherlock_ShadowPush));
    prologue.conv_i();
    prologue.calli(sigs.push);
    if (probe.onEnter()) {
        emitProbeCall(
            prologue, probe.cookie,
            reinterpret_cast<std::uintptr_t>(&Sherlock_ProbeEnter), sigs.probe);
    }

    // Widened branches and expanded returns have fixed sizes, so assign offsets in one pass.
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
                std::uint32_t tn; if (!mapOff(t, tn)) { return "a switch targets the middle of an instruction"; }
                il::put32(body, static_cast<std::uint32_t>(static_cast<std::int32_t>(tn) - static_cast<std::int32_t>(after)));
            }
            continue;
        }
        if (in.shortBr || in.longBr) {
            BYTE op = in.shortBr ? il::shortToLong(in.op0) : in.op0;
            body.push_back(op);
            std::uint32_t after = here + 5;
            std::uint32_t tn; if (!mapOff(in.brTarget, tn)) { return "a branch targets the middle of an instruction"; }
            il::put32(body, static_cast<std::uint32_t>(static_cast<std::int32_t>(tn) - static_cast<std::int32_t>(after)));
            continue;
        }
        body.insert(body.end(), in.raw, in.raw + in.len);
    }

    // Relocate EH offsets non-uniformly; reject offsets outside instruction boundaries.
    std::vector<il::EHClause> clauses;
    {
        std::vector<il::EHClause> original;
        il::parseEHClauses(header, code, codeSize, moreSects, original);
        // Original EH regions ending at codeSize must end at tryEnd, not endLabel:
        // crossing our outer try/finally boundary would produce illegal non-nested EH.
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
            if (!e) { return "an exception clause doesn't line up with its instructions"; }
            clauses.push_back(*e);
        }
    }
    clauses.push_back(il::EHClause{il::kClauseFinally, tryStart, tryEnd - tryStart, handlerStart, handlerLen, 0});

    // Only normal returns reach END; exceptional unwinds run the finally but not this hook.
    il::ILStream endStream;
    if (probe.onReturn()) {
        emitProbeCall(
            endStream, probe.cookie,
            reinterpret_cast<std::uintptr_t>(&Sherlock_ProbeReturn), sigs.probe);
    }
    if (nonVoid) endStream.ldloc(retLocalIndex);
    endStream.bytes().push_back(0x2A); // ret
    const std::vector<BYTE>& endSeq = endStream.bytes();

    const std::vector<BYTE>& prologueBytes = prologue.bytes();
    const std::vector<BYTE>& finallyBytes = finallyBody.bytes();
    const std::uint64_t newCodeSize64 =
        prologueBytes.size() + body.size() + finallyBytes.size() + endSeq.size();
    if (newCodeSize64 > std::numeric_limits<std::uint32_t>::max() ||
        maxStack > std::numeric_limits<std::uint16_t>::max() - 2) {
        return "the rewritten body would be too large";
    }
    const std::uint32_t newCodeSize = static_cast<std::uint32_t>(newCodeSize64);

    // Reject implausible growth: a layout bug must not hand malformed IL to the runtime.
    constexpr std::uint32_t kFixedOverhead = 256; // prologue + finally + end + slack
    if (newCodeSize > static_cast<std::uint64_t>(codeSize) * 2 + kFixedOverhead) {
        if (logger_)
            logger_->warn(
                "shadow rewrite produced an implausible body size ({} from {}); skipping method",
                newCodeSize, codeSize);
        return "the rewrite produced an implausible body size";
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

    return nullptr;
}

void ShadowStackInstrumenter::onModuleLoaded(ModuleID moduleId) {
    // ReJIT doesn't reach dynamic or collectible modules, and a dynamic module gains methods after it loads.
    DWORD flags = 0;
    LPCBYTE base = nullptr;
    ULONG nameLength = 0;
    AssemblyID assembly = 0;
    if (SUCCEEDED(info_->GetModuleInfo2(moduleId, &base, 0, &nameLength, nullptr, &assembly, &flags)) &&
        (flags & (COR_PRF_MODULE_DYNAMIC | COR_PRF_MODULE_COLLECTIBLE)) != 0) {
        std::lock_guard lock(jitMutex_);
        jitModules_.try_emplace(moduleId);
        return;
    }

    IMetaDataImport* md = nullptr;
    if (FAILED(info_->GetModuleMetaData(moduleId, ofRead, IID_IMetaDataImport, (IUnknown**)&md)) || md == nullptr)
        return;

    // EnumMethods needs a TypeDef; include <Module> for global methods.
    std::vector<ModuleID> reMods;
    std::vector<mdMethodDef> reToks;

    auto enumTypeMethods = [&](mdTypeDef td) {
        HCORENUM hm = nullptr;
        mdMethodDef toks[256];
        ULONG n = 0;
        while (SUCCEEDED(md->EnumMethods(&hm, td, toks, 256, &n)) && n > 0) {
            for (ULONG i = 0; i < n; ++i) {
                if (const char* reason = rejit::rejection(md, toks[i])) {
                    if (logger_) {
                        logger_->trace("not requesting ReJIT for token 0x{:08x}: {}", static_cast<unsigned>(toks[i]), reason);
                    }
                    continue;
                }
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
        logger_->trace(
            "shadow ReJIT requested {} method(s), hr=0x{:08x}",
            reToks.size(), static_cast<unsigned>(hr));
    }
}

void ShadowStackInstrumenter::onModuleUnloaded(ModuleID moduleId) {
    {
        std::lock_guard lock(sigMutex_);
        sigByModule_.erase(moduleId);
    }
    std::lock_guard lock(jitMutex_);
    jitModules_.erase(moduleId);
}

void ShadowStackInstrumenter::onJitCompilationStarted(ModuleID moduleId, mdMethodDef methodToken, const ProbePlan& probe) {
    {
        // Only the first compilation rewrites; tiered recompilations already see the rewritten IL.
        std::lock_guard lock(jitMutex_);
        auto module = jitModules_.find(moduleId);
        if (module == jitModules_.end() || !module->second.insert(methodToken).second) {
            return;
        }
    }
    std::vector<BYTE> out;
    if (prepare(moduleId, methodToken, probe, out) != nullptr) {
        return;
    }
    IMethodMalloc* allocator = nullptr;
    if (FAILED(info_->GetILFunctionBodyAllocator(moduleId, &allocator)) || allocator == nullptr) {
        skip(methodToken, "its module has no IL allocator");
        return;
    }
    void* body = allocator->Alloc(static_cast<ULONG>(out.size()));
    allocator->Release();
    if (body == nullptr) {
        skip(methodToken, "the IL allocator is out of memory");
        return;
    }
    std::memcpy(body, out.data(), out.size());
    if (FAILED(info_->SetILFunctionBody(moduleId, methodToken, static_cast<LPCBYTE>(body)))) {
        skip(methodToken, "the runtime refused the rewritten body");
        return;
    }
    instrumented_.fetch_add(1, std::memory_order_relaxed);
}

bool ShadowStackInstrumenter::rewrite(ModuleID moduleId, mdMethodDef methodToken,
                                      const ProbePlan& probe,
                                      ICorProfilerFunctionControl* control) {
    std::vector<BYTE> out;
    if (prepare(moduleId, methodToken, probe, out) != nullptr) {
        return false;
    }
    if (FAILED(control->SetILFunctionBody(static_cast<ULONG>(out.size()), out.data()))) {
        skip(methodToken, "the runtime refused the rewritten body");
        return false;
    }
    instrumented_.fetch_add(1, std::memory_order_relaxed);
    return true;
}

const char* ShadowStackInstrumenter::prepare(ModuleID moduleId, mdMethodDef methodToken, const ProbePlan& probe,
                                             std::vector<BYTE>& out) {
    // The circuit breaker is permanent for this process.
    if (disabled_.load(std::memory_order_relaxed)) {
        constexpr const char* kDisabled = "instrumentation was disabled after ReJIT errors";
        skip(methodToken, kDisabled);
        return kDisabled;
    }
    if (const char* reason = rejit::rejection(info_, moduleId, methodToken)) {
        skip(methodToken, reason);
        if (logger_) {
            logger_->warn("not rewriting token 0x{:08x}: {}", static_cast<unsigned>(methodToken), reason);
        }
        return reason;
    }
    // A declaration has stable metadata even when shared generic IL has no concrete FunctionID.
    const FrameId frameId = methods_.intern(moduleId, methodToken);
    if (const char* reason = buildIL(frameId, moduleId, methodToken, probe, out)) {
        skip(methodToken, reason);
        return reason;
    }
    return nullptr;
}

// Skipped methods are missing from stacks, so each is logged with its reason and the reasons are tallied.
void ShadowStackInstrumenter::skip(mdMethodDef methodToken, const char* reason) {
    skipped_.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard lock(skipMutex_);
        ++skipReasons_[reason];
    }
    if (logger_) {
        logger_->trace("not instrumenting token 0x{:08x}: {}", static_cast<unsigned>(methodToken), reason);
    }
}

std::string ShadowStackInstrumenter::skipSummary() const {
    std::lock_guard lock(skipMutex_);
    std::string summary;
    for (const auto& [reason, count] : skipReasons_) {
        summary += std::format("{}{} {}", summary.empty() ? "" : "; ", count, reason);
    }
    return summary;
}

void ShadowStackInstrumenter::noteReJITError() {
    // Leave already-compiled methods instrumented; only stop further rewrites.
    std::uint64_t n = rejitErrors_.fetch_add(1, std::memory_order_relaxed) + 1;
    if (n >= kMaxReJITErrors && !disabled_.exchange(true, std::memory_order_relaxed)) {
        if (logger_)
            logger_->error("shadow-stack instrumentation disabled after {} ReJIT errors; leaving remaining methods un-instrumented", n);
    }
}

} // namespace Sherlock
