#include "sherlock/profiler/probe.hpp"

#include "sherlock/common/logger.hpp"

#include <algorithm>
#include <cstdio>
#include <vector>

namespace Sherlock {

namespace {

// The single ProbeManager per process; the injected IL calls a free function
// trampoline with no client data, so it reaches the manager through this.
ProbeManager* g_probes = nullptr;

// Narrow ASCII std::string -> null-terminated WCHAR buffer (metadata names are
// effectively ASCII; this sidesteps the L"" wchar_t-width mismatch on the PAL).
std::vector<WCHAR> widen(const std::string& s) {
    std::vector<WCHAR> w;
    w.reserve(s.size() + 1);
    for (char c : s)
        w.push_back(static_cast<WCHAR>(static_cast<unsigned char>(c)));
    w.push_back(0);
    return w;
}

// Little-endian writers into a growing byte buffer.
void put16(std::vector<BYTE>& b, std::uint16_t v) {
    b.push_back(v & 0xFF);
    b.push_back((v >> 8) & 0xFF);
}
void put32(std::vector<BYTE>& b, std::uint32_t v) {
    for (int i = 0; i < 4; ++i) b.push_back((v >> (8 * i)) & 0xFF);
}
void put64(std::vector<BYTE>& b, std::uint64_t v) {
    for (int i = 0; i < 8; ++i) b.push_back((v >> (8 * i)) & 0xFF);
}

std::uint16_t rd16(const BYTE* p) { return p[0] | (p[1] << 8); }
std::uint32_t rd32(const BYTE* p) { return p[0] | (p[1] << 8) | (p[2] << 16) | (static_cast<std::uint32_t>(p[3]) << 24); }

// A normalized exception-handling clause (offsets are absolute into the code).
struct EHClause {
    std::uint32_t flags;
    std::uint32_t tryOffset;
    std::uint32_t tryLength;
    std::uint32_t handlerOffset;
    std::uint32_t handlerLength;
    std::uint32_t classTokenOrFilter;
};

constexpr std::uint32_t kClauseFilter = 0x0001; // COR_ILEXCEPTION_CLAUSE_FILTER

} // namespace

// The trampoline the rewritten IL calls (cdecl / IMAGE_CEE_CS_CALLCONV_C; on the
// 64-bit targets we build, every calling convention collapses to one anyway).
extern "C" void Sherlock_ProbeEnter(std::int32_t probeId) {
    if (g_probes)
        g_probes->onProbeHit(probeId);
}

ProbeManager::ProbeManager(ICorProfilerInfo10* info, Logger* logger)
    : info_(info), logger_(logger) {
    g_probes = this;
}

void ProbeManager::configure(const std::string& spec) {
    std::size_t start = 0;
    while (start <= spec.size()) {
        std::size_t end = spec.find_first_of(";,", start);
        std::string item = spec.substr(start, end == std::string::npos ? std::string::npos : end - start);
        start = (end == std::string::npos) ? spec.size() + 1 : end + 1;

        // Trim whitespace.
        while (!item.empty() && (item.front() == ' ' || item.front() == '\t')) item.erase(item.begin());
        while (!item.empty() && (item.back() == ' ' || item.back() == '\t')) item.pop_back();
        if (item.empty())
            continue;

        std::size_t dot = item.find_last_of('.');
        if (dot == std::string::npos || dot == 0 || dot + 1 >= item.size()) {
            if (logger_) logger_->logWarning("ignoring malformed probe spec (want Ns.Type.Method): " + item);
            continue;
        }
        specs_.push_back({item.substr(0, dot), item.substr(dot + 1), false});
    }
}

void ProbeManager::onModuleLoaded(ModuleID moduleId) {
    loadedModules_.push_back(moduleId); // remembered so armLive() can resolve against it later
    resolveInModule(moduleId);
}

bool ProbeManager::armLive(const std::string& spec) {
    const std::size_t before = armed_.size();
    configure(spec); // appends to specs_
    for (ModuleID m : loadedModules_)
        resolveInModule(m);
    return armed_.size() > before;
}

void ProbeManager::resolveInModule(ModuleID moduleId) {
    if (specs_.empty())
        return;

    IMetaDataImport* md = nullptr;
    if (FAILED(info_->GetModuleMetaData(moduleId, ofRead, IID_IMetaDataImport, (IUnknown**)&md)) || md == nullptr)
        return;

    std::vector<ModuleID> reMods;
    std::vector<mdMethodDef> reToks;

    for (Spec& s : specs_) {
        std::vector<WCHAR> typeW = widen(s.type);
        mdTypeDef td = mdTypeDefNil;
        if (FAILED(md->FindTypeDefByName(typeW.data(), mdTokenNil, &td)) || td == mdTypeDefNil)
            continue;

        std::vector<WCHAR> methodW = widen(s.method);
        HCORENUM hEnum = nullptr;
        mdMethodDef methods[64];
        ULONG count = 0;
        if (FAILED(md->EnumMethodsWithName(&hEnum, td, methodW.data(), methods, 64, &count))) {
            md->CloseEnum(hEnum);
            continue;
        }

        for (ULONG i = 0; i < count; ++i) {
            // Dedupe: a module loads once, but guard against re-arming anyway.
            bool already = std::any_of(armed_.begin(), armed_.end(), [&](const Armed& a) {
                return a.module == moduleId && a.token == methods[i];
            });
            if (already)
                continue;

            std::int32_t probeId = static_cast<std::int32_t>(armed_.size());
            armed_.push_back({moduleId, methods[i], probeId, s.type + "." + s.method});
            fired_.emplace_back(false);
            reMods.push_back(moduleId);
            reToks.push_back(methods[i]);
            s.resolved = true;
        }
        md->CloseEnum(hEnum);
    }
    md->Release();

    if (!reToks.empty()) {
        HRESULT hr = info_->RequestReJIT(static_cast<ULONG>(reToks.size()), reMods.data(), reToks.data());
        if (FAILED(hr) && logger_) {
            char buf[16];
            std::snprintf(buf, sizeof buf, "0x%08x", static_cast<unsigned>(hr));
            logger_->logError(std::string("RequestReJIT failed ") + buf);
        } else if (logger_) {
            logger_->logInfo("armed " + std::to_string(reToks.size()) + " method(s) for probing");
        }
    }
}

mdSignature ProbeManager::ensureProbeSig(ModuleID moduleId) {
    auto it = sigByModule_.find(moduleId);
    if (it != sigByModule_.end())
        return it->second;

    mdSignature tok = mdSignatureNil;
    IMetaDataEmit* emit = nullptr;
    if (SUCCEEDED(info_->GetModuleMetaData(moduleId, ofRead | ofWrite, IID_IMetaDataEmit, (IUnknown**)&emit)) && emit != nullptr) {
        // void Sherlock_ProbeEnter(int32) - unmanaged C calling convention.
        BYTE sig[] = {
            static_cast<BYTE>(IMAGE_CEE_CS_CALLCONV_C),
            0x01,                                  // param count
            static_cast<BYTE>(ELEMENT_TYPE_VOID),  // return
            static_cast<BYTE>(ELEMENT_TYPE_I4),    // param 0
        };
        emit->GetTokenFromSig(sig, sizeof sig, &tok);
        emit->Release();
    }
    sigByModule_[moduleId] = tok;
    return tok;
}

HRESULT ProbeManager::getReJITParameters(ModuleID moduleId, mdMethodDef methodId, ICorProfilerFunctionControl* control) {
    // Find the probe id assigned at arm time.
    std::int32_t probeId = -1;
    for (const Armed& a : armed_) {
        if (a.module == moduleId && a.token == methodId) { probeId = a.probeId; break; }
    }
    if (probeId < 0)
        return S_OK; // not ours; leave original IL

    mdSignature sigTok = ensureProbeSig(moduleId);
    if (sigTok == mdSignatureNil)
        return S_OK; // couldn't mint the calli signature — fall back to original

    LPCBYTE header = nullptr;
    ULONG headerSize = 0;
    if (FAILED(info_->GetILFunctionBody(moduleId, methodId, &header, &headerSize)) || header == nullptr)
        return S_OK;

    const BYTE* p = header;
    BYTE fmt = p[0] & CorILMethod_FormatMask;

    std::uint32_t codeSize;
    std::uint16_t maxStack;
    std::uint32_t localSig = 0;
    bool initLocals = false;
    bool moreSects = false;
    const BYTE* code = nullptr;

    if (fmt == CorILMethod_TinyFormat) {
        codeSize = p[0] >> 2;
        maxStack = 8;
        code = p + 1;
    } else if (fmt == CorILMethod_FatFormat) {
        std::uint16_t flags = rd16(p);
        std::uint16_t hdrDwords = flags >> 12;
        initLocals = (flags & CorILMethod_InitLocals) != 0;
        moreSects = (flags & CorILMethod_MoreSects) != 0;
        maxStack = rd16(p + 2);
        codeSize = rd32(p + 4);
        localSig = rd32(p + 8);
        code = p + hdrDwords * 4;
    } else {
        return S_OK; // unknown format — don't touch it
    }

    // The prologue we splice in: ldc.i4 <probeId>; ldc.i8 <&trampoline>; conv.i; calli <sig>.
    std::vector<BYTE> prefix;
    prefix.push_back(0x20); put32(prefix, static_cast<std::uint32_t>(probeId)); // ldc.i4
    prefix.push_back(0x21); put64(prefix, reinterpret_cast<std::uint64_t>(&Sherlock_ProbeEnter)); // ldc.i8
    prefix.push_back(0xD3); // conv.i
    prefix.push_back(0x29); put32(prefix, static_cast<std::uint32_t>(sigTok)); // calli
    const std::uint32_t prefixLen = static_cast<std::uint32_t>(prefix.size());

    // Collect + relocate EH clauses (offsets are absolute, so they shift by prefixLen).
    std::vector<EHClause> clauses;
    if (moreSects) {
        const BYTE* s = code + codeSize;
        // sections begin at the next 4-byte boundary after the code.
        std::size_t off = (s - header);
        s = header + ((off + 3) & ~static_cast<std::size_t>(3));
        bool more = true;
        while (more) {
            BYTE kind = s[0];
            bool fatSect = (kind & CorILMethod_Sect_FatFormat) != 0;
            more = (kind & CorILMethod_Sect_MoreSects) != 0;
            if ((kind & CorILMethod_Sect_KindMask) != CorILMethod_Sect_EHTable)
                break; // unknown section kind — stop (keeps us safe)

            const BYTE* clause;
            int n;
            std::size_t sectLen;
            if (fatSect) {
                std::uint32_t dataSize = s[1] | (s[2] << 8) | (s[3] << 16);
                n = static_cast<int>((dataSize - 4) / 24);
                clause = s + 4;
                sectLen = dataSize;
                for (int i = 0; i < n; ++i) {
                    const BYTE* c = clause + i * 24;
                    EHClause e{rd32(c), rd32(c + 4), rd32(c + 8), rd32(c + 12), rd32(c + 16), rd32(c + 20)};
                    e.tryOffset += prefixLen; e.handlerOffset += prefixLen;
                    if (e.flags & kClauseFilter) e.classTokenOrFilter += prefixLen;
                    clauses.push_back(e);
                }
            } else {
                std::uint32_t dataSize = s[1];
                n = static_cast<int>((dataSize - 4) / 12);
                clause = s + 4;
                sectLen = dataSize;
                for (int i = 0; i < n; ++i) {
                    const BYTE* c = clause + i * 12;
                    EHClause e{rd16(c), rd16(c + 2), c[4], rd16(c + 5), c[7], rd32(c + 8)};
                    e.tryOffset += prefixLen; e.handlerOffset += prefixLen;
                    if (e.flags & kClauseFilter) e.classTokenOrFilter += prefixLen;
                    clauses.push_back(e);
                }
            }
            s += (sectLen + 3) & ~static_cast<std::size_t>(3);
        }
    }

    // Emit a fat method: header + prefix + original code (+ relocated EH as fat section).
    std::vector<BYTE> out;
    std::uint16_t newFlags = CorILMethod_FatFormat;
    if (initLocals) newFlags |= CorILMethod_InitLocals;
    if (!clauses.empty()) newFlags |= CorILMethod_MoreSects;
    put16(out, static_cast<std::uint16_t>((newFlags & 0xFFF) | (3 << 12))); // flags + 3-dword header
    put16(out, std::max<std::uint16_t>(maxStack, 2));
    put32(out, codeSize + prefixLen);
    put32(out, localSig);
    out.insert(out.end(), prefix.begin(), prefix.end());
    out.insert(out.end(), code, code + codeSize);

    if (!clauses.empty()) {
        while (out.size() & 3) out.push_back(0); // 4-byte align the section
        out.push_back(CorILMethod_Sect_EHTable | CorILMethod_Sect_FatFormat);
        std::uint32_t dataSize = 4 + static_cast<std::uint32_t>(clauses.size()) * 24;
        out.push_back(dataSize & 0xFF);
        out.push_back((dataSize >> 8) & 0xFF);
        out.push_back((dataSize >> 16) & 0xFF);
        for (const EHClause& e : clauses) {
            put32(out, e.flags); put32(out, e.tryOffset); put32(out, e.tryLength);
            put32(out, e.handlerOffset); put32(out, e.handlerLength); put32(out, e.classTokenOrFilter);
        }
    }

    HRESULT hr = control->SetILFunctionBody(static_cast<ULONG>(out.size()), out.data());
    if (FAILED(hr) && logger_) {
        char buf[16];
        std::snprintf(buf, sizeof buf, "0x%08x", static_cast<unsigned>(hr));
        logger_->logError(std::string("SetILFunctionBody failed ") + buf);
    }
    return S_OK;
}

bool ProbeManager::isArmed(ModuleID moduleId, mdMethodDef token) const {
    return std::any_of(armed_.begin(), armed_.end(), [&](const Armed& a) {
        return a.module == moduleId && a.token == token;
    });
}

void ProbeManager::onProbeHit(std::int32_t probeId) {
    // Pure trigger: fire sl once, the first time this probe is hit. No recording - all
    // provenance comes from the heap snapshot sl takes in response.
    if (probeId >= 0 && static_cast<std::size_t>(probeId) < armed_.size() &&
        onHit_ && !fired_[probeId].exchange(true)) {
        onHit_(armed_[probeId].display);
    }
}

} // namespace Sherlock
