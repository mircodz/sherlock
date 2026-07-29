#include "sherlock/profiler/shadowstack.hpp"

#include "sherlock/common/logger.hpp"

#include <cstdio>
#include <cstring>
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

// ---- little-endian buffer writers (copied from probe.cpp) ----
void put16(std::vector<BYTE>& b, std::uint16_t v) { b.push_back(v & 0xFF); b.push_back((v >> 8) & 0xFF); }
void put32(std::vector<BYTE>& b, std::uint32_t v) { for (int i = 0; i < 4; ++i) b.push_back((v >> (8 * i)) & 0xFF); }
void put64(std::vector<BYTE>& b, std::uint64_t v) { for (int i = 0; i < 8; ++i) b.push_back((v >> (8 * i)) & 0xFF); }
std::uint16_t rd16(const BYTE* p) { return p[0] | (p[1] << 8); }
std::uint32_t rd32(const BYTE* p) { return p[0] | (p[1] << 8) | (p[2] << 16) | (static_cast<std::uint32_t>(p[3]) << 24); }

// ECMA-335 IL opcode operand lengths. Returns operand byte count for a 1-byte
// opcode, or -1 if the opcode is unknown/unsupported (=> skip the method). Two-byte
// (0xFE-prefixed) opcodes handled separately in the decoder. 0x45 (switch) is
// variable and flagged by the caller.
int operandLen1(BYTE op) {
    switch (op) {
        // no operand
        case 0x00: case 0x01: case 0x02: case 0x03: case 0x04: case 0x05:
        case 0x06: case 0x07: case 0x08: case 0x09: case 0x0A: case 0x0B:
        case 0x0C: case 0x0D: case 0x14: case 0x15: case 0x16: case 0x17:
        case 0x18: case 0x19: case 0x1A: case 0x1B: case 0x1C: case 0x1D:
        case 0x1E: case 0x25: case 0x26: case 0x2A: /*ret*/
        case 0x46: case 0x47: case 0x48: case 0x49: case 0x4A: case 0x4B:
        case 0x4C: case 0x4D: case 0x4E: case 0x4F: case 0x50: case 0x51:
        case 0x52: case 0x53: case 0x54: case 0x55: case 0x56: case 0x57:
        case 0x58: case 0x59: case 0x5A: case 0x5B: case 0x5C: case 0x5D:
        case 0x5E: case 0x5F: case 0x60: case 0x61: case 0x62: case 0x63:
        case 0x64: case 0x65: case 0x66: case 0x67: case 0x68: case 0x69:
        case 0x6A: case 0x6B: case 0x6C: case 0x6D: case 0x6E: case 0x76:
        case 0x7A: case 0x82: case 0x83: case 0x84: case 0x85: case 0x86:
        case 0x87: case 0x88: case 0x89: case 0x8A: case 0x8B:
        case 0x8E: case 0x90: case 0x91: case 0x92: case 0x93: case 0x94:
        case 0x95: case 0x96: case 0x97: case 0x98: case 0x99: case 0x9A:
        case 0x9B: case 0x9C: case 0x9D: case 0x9E: case 0x9F: case 0xA0:
        case 0xA1: case 0xA2: case 0xB3: case 0xB4: case 0xB5: case 0xB6:
        case 0xB7: case 0xB8: case 0xB9: case 0xBA: case 0xC3: case 0xCB:
        case 0xD1: case 0xD2: case 0xD3: case 0xD4: case 0xD5: case 0xD6:
        case 0xD7: case 0xD8: case 0xD9: case 0xDA: case 0xDB: case 0xDC: /*endfinally*/
        case 0xDF: case 0xE0:
            return 0;
        // 1-byte operand
        case 0x0E: case 0x0F: case 0x10: case 0x11: case 0x12: case 0x13:
        case 0x1F: /*ldc.i4.s*/ case 0x2B: /*br.s*/ case 0x2C: case 0x2D:
        case 0x2E: case 0x2F: case 0x30: case 0x31: case 0x32: case 0x33:
        case 0x34: case 0x35: case 0x36: case 0x37: case 0xDE: /*leave.s*/
            return 1;
        // 4-byte operand
        case 0x20: /*ldc.i4*/ case 0x22: /*ldc.r4*/ case 0x27: /*jmp*/
        case 0x28: /*call*/ case 0x29: /*calli*/ case 0x38: /*br*/
        case 0x39: case 0x3A: case 0x3B: case 0x3C: case 0x3D: case 0x3E:
        case 0x3F: case 0x40: case 0x41: case 0x42: case 0x43: case 0x44:
        case 0x6F: /*callvirt*/ case 0x70: case 0x71: case 0x72: case 0x73:
        case 0x74: case 0x75: case 0x79: case 0x7B: case 0x7C: case 0x7D:
        case 0x7E: case 0x7F: case 0x80: case 0x81: case 0x8C: /*box*/ case 0x8D: case 0x8F:
        case 0xA3: case 0xA4: case 0xA5: case 0xC2: case 0xC6: case 0xD0:
        case 0xDD: /*leave*/
            return 4;
        // 8-byte operand
        case 0x21: /*ldc.i8*/ case 0x23: /*ldc.r8*/
            return 8;
        default:
            return -1; // unknown => skip method
    }
}

// Two-byte 0xFE-prefixed opcode operand length (second byte passed). -1 = unknown.
int operandLen2(BYTE op2) {
    switch (op2) {
        case 0x00: case 0x01: case 0x02: case 0x03: case 0x04: case 0x05:
        case 0x0F: case 0x11: case 0x13: case 0x14: case 0x17: case 0x18:
        case 0x1A: case 0x1D: case 0x1E:
            return 0;
        case 0x12: case 0x19: // unaligned., no.
            return 1;
        case 0x09: case 0x0A: case 0x0B: case 0x0C: case 0x0D: case 0x0E: // ldarg/ldloc/starg/stloc
            return 2;
        case 0x06: case 0x07: case 0x15: case 0x16: case 0x1C: // ldftn/ldvirtftn/initobj/constrained./sizeof
            return 4;
        default:
            return -1;
    }
}

bool isRet(BYTE op) { return op == 0x2A; }
bool isSwitch(BYTE op) { return op == 0x45; }
// Short single-byte branches 0x2B..0x37 and leave.s 0xDE; long 0x38..0x44 and leave 0xDD.
bool isShortBranch(BYTE op) { return (op >= 0x2B && op <= 0x37) || op == 0xDE; }
bool isLongBranch(BYTE op) { return (op >= 0x38 && op <= 0x44) || op == 0xDD; }
// Map a short branch opcode to its long equivalent (so all branches become 4-byte
// and we avoid iterative offset convergence).
BYTE shortToLong(BYTE op) {
    if (op == 0xDE) return 0xDD;         // leave.s -> leave
    return op + 0x0D;                    // br.s(0x2B)->br(0x38), cond .s -> long
}

// A decoded body instruction.
struct Insn {
    std::uint32_t off;      // original offset in code
    std::uint32_t len;      // total bytes (opcode + operand)
    BYTE op0;               // first opcode byte (0xFE for two-byte)
    BYTE op1;               // second byte if two-byte, else 0
    bool twoByte;
    bool ret;
    bool sw;                // switch
    bool shortBr;
    bool longBr;
    std::uint32_t brTarget; // absolute original offset (for short/long branch)
    std::vector<std::uint32_t> swTargets; // absolute original offsets (switch)
    const BYTE* raw;        // pointer to original bytes
};

// A normalized EH clause (absolute offsets), same shape as probe.cpp.
struct EHClause {
    std::uint32_t flags, tryOffset, tryLength, handlerOffset, handlerLength, classTokenOrFilter;
};
constexpr std::uint32_t kClauseFilter = 0x0001;
constexpr std::uint32_t kClauseFinally = 0x0002;

// Compressed-integer encode (ECMA-335 II.23.2).
void compress(std::vector<BYTE>& b, std::uint32_t v) {
    if (v <= 0x7F) { b.push_back(v); }
    else if (v <= 0x3FFF) { b.push_back(0x80 | (v >> 8)); b.push_back(v & 0xFF); }
    else { b.push_back(0xC0 | (v >> 24)); b.push_back((v >> 16) & 0xFF); b.push_back((v >> 8) & 0xFF); b.push_back(v & 0xFF); }
}
// Compressed-integer decode; advances p. Returns false on malformed.
bool uncompress(const BYTE*& p, const BYTE* end, std::uint32_t& out) {
    if (p >= end) return false;
    BYTE b0 = *p;
    if ((b0 & 0x80) == 0) { out = b0; p += 1; return true; }
    if ((b0 & 0xC0) == 0x80) { if (p + 2 > end) return false; out = ((b0 & 0x3F) << 8) | p[1]; p += 2; return true; }
    if ((b0 & 0xE0) == 0xC0) { if (p + 4 > end) return false; out = ((b0 & 0x1F) << 24) | (p[1] << 16) | (p[2] << 8) | p[3]; p += 4; return true; }
    return false;
}

} // namespace

ShadowStackInstrumenter::ShadowStackInstrumenter(ICorProfilerInfo10* info, Logger* logger)
    : info_(info), logger_(logger) {}

ShadowStackInstrumenter::ModuleSigs& ShadowStackInstrumenter::ensureSigs(ModuleID moduleId) {
    auto it = sigByModule_.find(moduleId);
    if (it != sigByModule_.end())
        return it->second;

    ModuleSigs sigs;
    IMetaDataEmit* emit = nullptr;
    if (SUCCEEDED(info_->GetModuleMetaData(moduleId, ofRead | ofWrite, IID_IMetaDataEmit, (IUnknown**)&emit)) && emit != nullptr) {
        // void Sherlock_ShadowPush(int64) — unmanaged C calling convention.
        BYTE pushSig[] = { static_cast<BYTE>(IMAGE_CEE_CS_CALLCONV_C), 0x01,
                           static_cast<BYTE>(ELEMENT_TYPE_VOID), static_cast<BYTE>(ELEMENT_TYPE_I8) };
        emit->GetTokenFromSig(pushSig, sizeof pushSig, &sigs.push);
        // void Sherlock_ShadowPop()
        BYTE popSig[] = { static_cast<BYTE>(IMAGE_CEE_CS_CALLCONV_C), 0x00,
                          static_cast<BYTE>(ELEMENT_TYPE_VOID) };
        emit->GetTokenFromSig(popSig, sizeof popSig, &sigs.pop);
        emit->Release();
    }
    return sigByModule_.emplace(moduleId, sigs).first->second;
}

bool ShadowStackInstrumenter::moduleAllowed(ModuleID moduleId) {
    if (moduleFilter_.empty())
        return true;
    WCHAR nameBuf[512];
    ULONG nameLen = 0;
    LPCBYTE base = nullptr;
    AssemblyID asmId = 0;
    if (FAILED(info_->GetModuleInfo(moduleId, &base, 512, &nameLen, nameBuf, &asmId)))
        return false;
    std::string modName;
    for (ULONG i = 0; i < nameLen && nameBuf[i] != 0; ++i)
        modName.push_back(nameBuf[i] < 128 ? static_cast<char>(nameBuf[i]) : '?');
    return modName.find(moduleFilter_) != std::string::npos;
}

// Core IL rewrite shared by the JIT and ReJIT paths. Fills `out` with a fully assembled
// method body (fat header + try/finally-wrapped code + EH section) and returns true; returns
// false to skip (caller leaves the original IL untouched). Never sets the body itself.
bool ShadowStackInstrumenter::buildIL(FunctionID functionId, ModuleID moduleId,
                                      mdMethodDef methodToken, std::vector<BYTE>& out) {
    ModuleSigs& sigs = ensureSigs(moduleId);
    if (sigs.push == mdSignatureNil || sigs.pop == mdSignatureNil) { return false; }

    LPCBYTE header = nullptr;
    ULONG headerSize = 0;
    if (FAILED(info_->GetILFunctionBody(moduleId, methodToken, &header, &headerSize)) || header == nullptr) {
        return false;
    }

    const BYTE* p = header;
    BYTE fmt = p[0] & CorILMethod_FormatMask;
    std::uint32_t codeSize;
    std::uint16_t maxStack;
    std::uint32_t localSigTok = 0;
    bool initLocals = false, moreSects = false;
    const BYTE* code = nullptr;

    if ((p[0] & 0x03) == CorILMethod_TinyFormat) {
        // Tiny format: low 2 bits == 0b10 (covers both TinyFormat and TinyFormat1 for
        // odd code sizes); size is the high 6 bits of the single header byte.
        codeSize = p[0] >> 2; maxStack = 8; code = p + 1;
    } else if (fmt == CorILMethod_FatFormat) {
        std::uint16_t flags = rd16(p);
        std::uint16_t hdrDwords = flags >> 12;
        initLocals = (flags & CorILMethod_InitLocals) != 0;
        moreSects = (flags & CorILMethod_MoreSects) != 0;
        maxStack = rd16(p + 2);
        codeSize = rd32(p + 4);
        localSigTok = rd32(p + 8);
        code = p + hdrDwords * 4;
    } else {
        return false;
    }

    // ---- decode the body ----
    std::vector<Insn> insns;
    std::uint32_t o = 0;
    bool ok = true;
    bool unsafeToWrap = false; // constructs illegal inside a try/finally region
    while (o < codeSize) {
        Insn in{};
        in.off = o;
        in.raw = code + o;
        BYTE b = code[o];
        if (b == 0xFE) {
            if (o + 1 >= codeSize) { ok = false; break; }
            BYTE b2 = code[o + 1];
            int ol = operandLen2(b2);
            if (ol < 0) { ok = false; break; }
            // tail. (0x14) and localloc (0x0F): a tail call must be immediately followed by
            // ret (our ret->leave rewrite would break it); localloc requires an empty eval
            // stack and can't live inside a try. Leave such methods un-instrumented.
            if (b2 == 0x14 || b2 == 0x0F) unsafeToWrap = true;
            in.twoByte = true; in.op0 = 0xFE; in.op1 = b2; in.len = 2 + ol;
        } else if (isSwitch(b)) {
            if (o + 5 > codeSize) { ok = false; break; }
            std::uint32_t n = rd32(code + o + 1);
            std::uint32_t base = o + 5 + n * 4;
            if (base > codeSize) { ok = false; break; }
            in.op0 = b; in.sw = true; in.len = 5 + n * 4;
            for (std::uint32_t i = 0; i < n; ++i) {
                std::int32_t rel = static_cast<std::int32_t>(rd32(code + o + 5 + i * 4));
                in.swTargets.push_back(base + rel);
            }
        } else {
            int ol = operandLen1(b);
            if (ol < 0) { ok = false; break; }
            if (b == 0x27) unsafeToWrap = true; // jmp: method-exit transfer, illegal in a try
            in.op0 = b; in.len = 1 + ol;
            in.ret = isRet(b);
            if (isShortBranch(b)) {
                in.shortBr = true;
                std::int8_t rel = static_cast<std::int8_t>(code[o + 1]);
                in.brTarget = (o + 2) + rel;
            } else if (isLongBranch(b)) {
                in.longBr = true;
                std::int32_t rel = static_cast<std::int32_t>(rd32(code + o + 1));
                in.brTarget = (o + 5) + rel;
            }
        }
        if (o + in.len > codeSize) { ok = false; break; }
        insns.push_back(in);
        o += in.len;
    }
    if (!ok || insns.empty() || unsafeToWrap) { return false; }

    // ---- determine return type: extract the RetType blob from the method sig, so we
    // can add a local to stash the returned value before `leave`. Void => no local. ----
    bool nonVoid = false;
    std::vector<BYTE> retTypeBlob;
    {
        IMetaDataImport* md = nullptr;
        if (SUCCEEDED(info_->GetModuleMetaData(moduleId, ofRead, IID_IMetaDataImport, (IUnknown**)&md)) && md) {
            PCCOR_SIGNATURE sig = nullptr; ULONG sigLen = 0;
            if (SUCCEEDED(md->GetMethodProps(methodToken, nullptr, nullptr, 0, nullptr, nullptr, &sig, &sigLen, nullptr, nullptr))) {
                const BYTE* sp = sig; const BYTE* send = sig + sigLen;
                if (sp < send) {
                    BYTE cc = *sp++;                      // calling convention
                    std::uint32_t tmp;
                    if (cc & IMAGE_CEE_CS_CALLCONV_GENERIC) { if (!uncompress(sp, send, tmp)) { md->Release(); return false; } }
                    if (!uncompress(sp, send, tmp)) { md->Release(); return false; } // param count
                    // RetType begins here. Parse just enough to measure its length; skip on anything exotic.
                    const BYTE* rtStart = sp;
                    // custom mods
                    while (sp < send && (*sp == ELEMENT_TYPE_CMOD_OPT || *sp == ELEMENT_TYPE_CMOD_REQD)) {
                        sp++; std::uint32_t t; if (!uncompress(sp, send, t)) { md->Release(); return false; }
                    }
                    if (sp >= send) { md->Release(); return false; }
                    BYTE et = *sp++;
                    if (et == ELEMENT_TYPE_VOID) {
                        nonVoid = false;
                    } else if (et == ELEMENT_TYPE_BYREF || et == ELEMENT_TYPE_TYPEDBYREF) {
                        // ref returns / typedref: skip (uncommon, tricky).
                        md->Release(); return false;
                    } else {
                        // Simple value/ref element types we can size to one byte, plus CLASS/VALUETYPE
                        // (+token) and SZARRAY-of-simple. Anything else: skip.
                        bool simple = false;
                        switch (et) {
                            case ELEMENT_TYPE_BOOLEAN: case ELEMENT_TYPE_CHAR:
                            case ELEMENT_TYPE_I1: case ELEMENT_TYPE_U1: case ELEMENT_TYPE_I2: case ELEMENT_TYPE_U2:
                            case ELEMENT_TYPE_I4: case ELEMENT_TYPE_U4: case ELEMENT_TYPE_I8: case ELEMENT_TYPE_U8:
                            case ELEMENT_TYPE_R4: case ELEMENT_TYPE_R8: case ELEMENT_TYPE_I: case ELEMENT_TYPE_U:
                            case ELEMENT_TYPE_STRING: case ELEMENT_TYPE_OBJECT:
                                simple = true; break;
                            case ELEMENT_TYPE_CLASS: case ELEMENT_TYPE_VALUETYPE: {
                                std::uint32_t t; if (!uncompress(sp, send, t)) { md->Release(); return false; }
                                simple = true; break;
                            }
                            default: break;
                        }
                        if (!simple) { md->Release(); return false; }
                        nonVoid = true;
                        retTypeBlob.assign(rtStart, sp);
                    }
                }
            }
            md->Release();
        }
    }

    // ---- build a new LocalVarSig = original locals + (optional) one retType local ----
    std::uint32_t origLocalCount = 0;
    std::uint32_t retLocalIndex = 0;
    mdSignature newLocalTok = static_cast<mdSignature>(localSigTok);
    if (nonVoid) {
        IMetaDataImport* mdi = nullptr;
        IMetaDataEmit* mde = nullptr;
        std::vector<BYTE> origLocalsBody; // bytes after callconv+count
        if (localSigTok != 0) {
            if (SUCCEEDED(info_->GetModuleMetaData(moduleId, ofRead, IID_IMetaDataImport, (IUnknown**)&mdi)) && mdi) {
                PCCOR_SIGNATURE ls = nullptr; ULONG lsLen = 0;
                if (SUCCEEDED(mdi->GetSigFromToken(static_cast<mdSignature>(localSigTok), &ls, &lsLen)) && lsLen >= 2) {
                    const BYTE* lp = ls; const BYTE* lend = ls + lsLen;
                    lp++; // LOCAL_SIG (0x07)
                    if (!uncompress(lp, lend, origLocalCount)) { if (mdi) mdi->Release(); return false; }
                    origLocalsBody.assign(lp, lend);
                }
                mdi->Release();
            }
        }
        retLocalIndex = origLocalCount;
        std::vector<BYTE> newSig;
        newSig.push_back(0x07); // IMAGE_CEE_CS_CALLCONV_LOCAL_SIG
        compress(newSig, origLocalCount + 1);
        newSig.insert(newSig.end(), origLocalsBody.begin(), origLocalsBody.end());
        newSig.insert(newSig.end(), retTypeBlob.begin(), retTypeBlob.end());
        if (SUCCEEDED(info_->GetModuleMetaData(moduleId, ofRead | ofWrite, IID_IMetaDataEmit, (IUnknown**)&mde)) && mde) {
            mde->GetTokenFromSig(newSig.data(), static_cast<ULONG>(newSig.size()), &newLocalTok);
            mde->Release();
        } else {
            return false;
        }
    }

    // ---- helpers to emit stloc/ldloc for retLocalIndex ----
    auto emitStloc = [&](std::vector<BYTE>& b, std::uint32_t idx) {
        if (idx <= 0xFF) { b.push_back(0x13); b.push_back(idx & 0xFF); }        // stloc.s
        else { b.push_back(0xFE); b.push_back(0x0E); put16(b, idx & 0xFFFF); }  // stloc
    };
    auto emitLdloc = [&](std::vector<BYTE>& b, std::uint32_t idx) {
        if (idx <= 0xFF) { b.push_back(0x11); b.push_back(idx & 0xFF); }        // ldloc.s
        else { b.push_back(0xFE); b.push_back(0x0C); put16(b, idx & 0xFFFF); }  // ldloc
    };

    // ---- layout pass 1: assign new offsets ----
    // Output segments (in order):
    //   [prologue]  ldc.i8 funcId; ldc.i8 &push; conv.i; calli pushSig
    //   [TRY]       transformed body (ret -> [stloc ret]; leave END)
    //   [FINALLY]   ldc.i8 &pop; conv.i; calli popSig; endfinally
    //   [END]       (ldloc ret;) ret
    std::vector<BYTE> prologue;
    prologue.push_back(0x21); put64(prologue, static_cast<std::uint64_t>(functionId));      // ldc.i8 funcId
    prologue.push_back(0x21); put64(prologue, reinterpret_cast<std::uint64_t>(&Sherlock_ShadowPush)); // ldc.i8 &push
    prologue.push_back(0xD3);                                                                // conv.i
    prologue.push_back(0x29); put32(prologue, static_cast<std::uint32_t>(sigs.push));        // calli

    // Per-body-instruction new offset map. We know each transformed instruction's size
    // up front (branches all long-form; ret expands), so we can assign offsets directly.
    std::uint32_t tryStart = static_cast<std::uint32_t>(prologue.size());
    std::vector<std::uint32_t> newOff(insns.size());
    auto transformedLen = [&](const Insn& in) -> std::uint32_t {
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
    std::vector<BYTE> finallyBody;
    finallyBody.push_back(0x21); put64(finallyBody, reinterpret_cast<std::uint64_t>(&Sherlock_ShadowPop)); // ldc.i8 &pop
    finallyBody.push_back(0xD3);                                                                // conv.i
    finallyBody.push_back(0x29); put32(finallyBody, static_cast<std::uint32_t>(sigs.pop));      // calli
    finallyBody.push_back(0xDC);                                                                // endfinally
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
    std::vector<BYTE> body;
    body.reserve(codeSize + insns.size());
    for (std::size_t i = 0; i < insns.size(); ++i) {
        const Insn& in = insns[i];
        std::uint32_t here = newOff[i];
        if (in.ret) {
            if (nonVoid) emitStloc(body, retLocalIndex);
            std::uint32_t leaveHere = tryStart + (static_cast<std::uint32_t>(body.size())); // absolute of this leave
            std::uint32_t after = leaveHere + 5;
            std::int32_t rel = static_cast<std::int32_t>(endLabel) - static_cast<std::int32_t>(after);
            body.push_back(0xDD); put32(body, static_cast<std::uint32_t>(rel)); // leave END
            continue;
        }
        if (in.sw) {
            body.push_back(0x45);
            std::uint32_t n = static_cast<std::uint32_t>(in.swTargets.size());
            put32(body, n);
            std::uint32_t after = here + 5 + n * 4;
            for (std::uint32_t t : in.swTargets) {
                std::uint32_t tn; if (!mapOff(t, tn)) { return false; }
                put32(body, static_cast<std::uint32_t>(static_cast<std::int32_t>(tn) - static_cast<std::int32_t>(after)));
            }
            continue;
        }
        if (in.shortBr || in.longBr) {
            BYTE op = in.shortBr ? shortToLong(in.op0) : in.op0;
            body.push_back(op);
            std::uint32_t after = here + 5;
            std::uint32_t tn; if (!mapOff(in.brTarget, tn)) { return false; }
            put32(body, static_cast<std::uint32_t>(static_cast<std::int32_t>(tn) - static_cast<std::int32_t>(after)));
            continue;
        }
        // plain instruction: copy raw bytes verbatim
        body.insert(body.end(), in.raw, in.raw + in.len);
    }

    // ---- relocate original EH clauses into the new offset space ----
    std::vector<EHClause> clauses;
    if (moreSects) {
        const BYTE* s = code + codeSize;
        std::size_t off = (s - header);
        s = header + ((off + 3) & ~static_cast<std::size_t>(3));
        bool more = true;
        while (more) {
            BYTE kind = s[0];
            bool fatSect = (kind & CorILMethod_Sect_FatFormat) != 0;
            more = (kind & CorILMethod_Sect_MoreSects) != 0;
            if ((kind & CorILMethod_Sect_KindMask) != CorILMethod_Sect_EHTable) break;
            int n; const BYTE* clause; std::size_t sectLen;
            auto relocate = [&](EHClause e) -> std::optional<EHClause> {
                std::uint32_t ts, te, hs, he;
                if (!mapOff(e.tryOffset, ts)) return std::nullopt;
                if (!mapOff(e.tryOffset + e.tryLength, te)) return std::nullopt;
                if (!mapOff(e.handlerOffset, hs)) return std::nullopt;
                if (!mapOff(e.handlerOffset + e.handlerLength, he)) return std::nullopt;
                e.tryOffset = ts; e.tryLength = te - ts;
                e.handlerOffset = hs; e.handlerLength = he - hs;
                if (e.flags & kClauseFilter) { std::uint32_t f; if (!mapOff(e.classTokenOrFilter, f)) return std::nullopt; e.classTokenOrFilter = f; }
                return e;
            };
            if (fatSect) {
                std::uint32_t dataSize = s[1] | (s[2] << 8) | (s[3] << 16);
                n = static_cast<int>((dataSize - 4) / 24); clause = s + 4; sectLen = dataSize;
                for (int i = 0; i < n; ++i) {
                    const BYTE* c = clause + i * 24;
                    auto e = relocate(EHClause{rd32(c), rd32(c + 4), rd32(c + 8), rd32(c + 12), rd32(c + 16), rd32(c + 20)});
                    if (!e) { return false; }
                    clauses.push_back(*e);
                }
            } else {
                std::uint32_t dataSize = s[1];
                n = static_cast<int>((dataSize - 4) / 12); clause = s + 4; sectLen = dataSize;
                for (int i = 0; i < n; ++i) {
                    const BYTE* c = clause + i * 12;
                    auto e = relocate(EHClause{rd16(c), rd16(c + 2), c[4], rd16(c + 5), c[7], rd32(c + 8)});
                    if (!e) { return false; }
                    clauses.push_back(*e);
                }
            }
            s += (sectLen + 3) & ~static_cast<std::size_t>(3);
        }
    }
    // Add our finally clause covering the whole try.
    clauses.push_back(EHClause{kClauseFinally, tryStart, tryEnd - tryStart, handlerStart, handlerLen, 0});

    // ---- END sequence ----
    std::vector<BYTE> endSeq;
    if (nonVoid) emitLdloc(endSeq, retLocalIndex);
    endSeq.push_back(0x2A); // ret

    // ---- assemble the full method ----
    std::uint32_t newCodeSize = static_cast<std::uint32_t>(prologue.size() + body.size() + finallyBody.size() + endSeq.size());
    out.clear();
    std::uint16_t newFlags = CorILMethod_FatFormat | CorILMethod_MoreSects;
    if (initLocals) newFlags |= CorILMethod_InitLocals;
    put16(out, static_cast<std::uint16_t>((newFlags & 0xFFF) | (3 << 12)));
    put16(out, static_cast<std::uint16_t>(maxStack + 2));
    put32(out, newCodeSize);
    put32(out, static_cast<std::uint32_t>(newLocalTok));
    out.insert(out.end(), prologue.begin(), prologue.end());
    out.insert(out.end(), body.begin(), body.end());
    out.insert(out.end(), finallyBody.begin(), finallyBody.end());
    out.insert(out.end(), endSeq.begin(), endSeq.end());

    // EH section (fat), 4-byte aligned.
    while (out.size() & 3) out.push_back(0);
    out.push_back(CorILMethod_Sect_EHTable | CorILMethod_Sect_FatFormat);
    std::uint32_t dataSize = 4 + static_cast<std::uint32_t>(clauses.size()) * 24;
    out.push_back(dataSize & 0xFF); out.push_back((dataSize >> 8) & 0xFF); out.push_back((dataSize >> 16) & 0xFF);
    for (const EHClause& e : clauses) {
        put32(out, e.flags); put32(out, e.tryOffset); put32(out, e.tryLength);
        put32(out, e.handlerOffset); put32(out, e.handlerLength); put32(out, e.classTokenOrFilter);
    }

    return true;
}

// --- ReJIT path --------------------------------------------------------------------------
void ShadowStackInstrumenter::onModuleLoaded(ModuleID moduleId) {
    if (!moduleAllowed(moduleId))
        return;

    IMetaDataImport* md = nullptr;
    if (FAILED(info_->GetModuleMetaData(moduleId, ofRead, IID_IMetaDataImport, (IUnknown**)&md)) || md == nullptr)
        return;

    // Enumerate every method defined in this module and request a ReJIT for each. The rewritten
    // IL is supplied later from getReJITParameters (called by the runtime before first use, and
    // used for inlined bodies too — so we can leave inlining ON). EnumMethods needs a concrete
    // typedef, so walk all type defs first (plus the module's <Module> global-methods pseudo-type).
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
        char buf[16];
        std::snprintf(buf, sizeof buf, "0x%08x", static_cast<unsigned>(hr));
        logger_->logInfo("shadow ReJIT requested " + std::to_string(reToks.size()) +
                         " method(s), hr=" + buf);
    }
}

HRESULT ShadowStackInstrumenter::getReJITParameters(ModuleID moduleId, mdMethodDef methodToken,
                                                    ICorProfilerFunctionControl* control) {
    // Resolve the FunctionID so buildIL can bake it into the push. GetFunctionFromToken gives the
    // canonical (non-generic) FunctionID; generic instantiations share this body's IL, which is
    // fine — the shadow frame just identifies the method, resolved to a name at dump time.
    FunctionID functionId = 0;
    if (FAILED(info_->GetFunctionFromToken(moduleId, methodToken, &functionId)))
        functionId = 0; // still emit; a 0 frame resolves to <unknown> but keeps depth correct

    std::vector<BYTE> out;
    if (!buildIL(functionId, moduleId, methodToken, out)) { skipped_++; return S_OK; }
    if (FAILED(control->SetILFunctionBody(static_cast<ULONG>(out.size()), out.data()))) {
        skipped_++; return S_OK;
    }
    instrumented_++;
    return S_OK;
}

} // namespace Sherlock
