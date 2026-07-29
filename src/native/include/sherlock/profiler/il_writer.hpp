#pragma once

// Shared IL-emit / IL-parse primitives used by both the trigger probe (probe.cpp) and the shadow-stack
// instrumenter (shadowstack.cpp). Both rewrite method bodies via ReJIT; the little-endian byte plumbing,
// the EH-clause representation, and the method's exception-section scanner are identical between them.
// The two differ only in HOW they transform the body (probe splices a prologue and shifts EH offsets by a
// constant; shadow wraps the whole body in try/finally and remaps every offset), so that transform stays
// in each caller — only the shared substrate lives here.

#include <cstdint>
#include <vector>

#include "profilercommon.h"

namespace Sherlock::il {

// ---- little-endian writers into a growing byte buffer ----
inline void put16(std::vector<BYTE>& b, std::uint16_t v) {
    b.push_back(v & 0xFF);
    b.push_back((v >> 8) & 0xFF);
}
inline void put32(std::vector<BYTE>& b, std::uint32_t v) {
    for (int i = 0; i < 4; ++i) b.push_back((v >> (8 * i)) & 0xFF);
}
inline void put64(std::vector<BYTE>& b, std::uint64_t v) {
    for (int i = 0; i < 8; ++i) b.push_back((v >> (8 * i)) & 0xFF);
}

// ---- little-endian readers ----
inline std::uint16_t rd16(const BYTE* p) { return p[0] | (p[1] << 8); }
inline std::uint32_t rd32(const BYTE* p) {
    return p[0] | (p[1] << 8) | (p[2] << 16) | (static_cast<std::uint32_t>(p[3]) << 24);
}

// A normalized exception-handling clause. Offsets are absolute into the method's code, exactly as read
// from the metadata section — callers relocate them into their rewritten body.
struct EHClause {
    std::uint32_t flags;
    std::uint32_t tryOffset;
    std::uint32_t tryLength;
    std::uint32_t handlerOffset;
    std::uint32_t handlerLength;
    std::uint32_t classTokenOrFilter;
};

constexpr std::uint32_t kClauseFilter = 0x0001;  // COR_ILEXCEPTION_CLAUSE_FILTER
constexpr std::uint32_t kClauseFinally = 0x0002; // COR_ILEXCEPTION_CLAUSE_FINALLY

// A growable IL byte stream with named emitters for the handful of opcodes the two instrumenters splice
// in. This keeps the injected prologue/finally readable as the IL it is (`s.ldc_i8(fn); s.calli(sig)`)
// instead of scattered magic bytes, and centralizes the opcode constants. It is a thin wrapper over a
// byte vector — `bytes()` exposes the buffer for the parts still emitted by hand (raw instruction copies,
// branch fixups computed against absolute offsets).
class ILStream {
public:
    std::vector<BYTE>& bytes() { return b_; }
    const std::vector<BYTE>& bytes() const { return b_; }
    std::size_t size() const { return b_.size(); }

    void ldc_i4(std::uint32_t v)  { b_.push_back(0x20); put32(b_, v); }
    void ldc_i8(std::uint64_t v)  { b_.push_back(0x21); put64(b_, v); }
    void conv_i()                 { b_.push_back(0xD3); }
    void calli(mdSignature sig)   { b_.push_back(0x29); put32(b_, static_cast<std::uint32_t>(sig)); }
    void endfinally()             { b_.push_back(0xDC); }

    // stloc/ldloc pick the short (.s, 1-byte index) form when the local index fits in a byte, else the
    // two-byte 0xFE-prefixed long form.
    void stloc(std::uint32_t idx) {
        if (idx <= 0xFF) { b_.push_back(0x13); b_.push_back(idx & 0xFF); }
        else { b_.push_back(0xFE); b_.push_back(0x0E); put16(b_, idx & 0xFFFF); }
    }
    void ldloc(std::uint32_t idx) {
        if (idx <= 0xFF) { b_.push_back(0x11); b_.push_back(idx & 0xFF); }
        else { b_.push_back(0xFE); b_.push_back(0x0C); put16(b_, idx & 0xFFFF); }
    }

    // `leave <int32>` with the 4-byte relative displacement already computed by the caller.
    void leave_rel(std::int32_t rel) { b_.push_back(0xDD); put32(b_, static_cast<std::uint32_t>(rel)); }

private:
    std::vector<BYTE> b_;
};

// Parse the method's exception-handling section(s) into `out`, appending one EHClause per clause with its
// original (absolute) offsets. `header` is the method body start, `code`/`codeSize` locate the IL; `moreSects`
// is the fat header's MoreSects flag (no sections when false). Stops at the first non-EH section kind, which
// keeps an unrecognized section from being misread. Both fat and thin (small) clause formats are handled.
inline void parseEHClauses(const BYTE* header, const BYTE* code, std::uint32_t codeSize,
                           bool moreSects, std::vector<EHClause>& out) {
    if (!moreSects)
        return;

    const BYTE* s = code + codeSize;
    // Sections begin at the next 4-byte boundary after the code.
    std::size_t off = (s - header);
    s = header + ((off + 3) & ~static_cast<std::size_t>(3));
    bool more = true;
    while (more) {
        BYTE kind = s[0];
        bool fatSect = (kind & CorILMethod_Sect_FatFormat) != 0;
        more = (kind & CorILMethod_Sect_MoreSects) != 0;
        if ((kind & CorILMethod_Sect_KindMask) != CorILMethod_Sect_EHTable)
            break; // unknown section kind — stop (keeps us safe)

        std::size_t sectLen;
        if (fatSect) {
            std::uint32_t dataSize = s[1] | (s[2] << 8) | (s[3] << 16);
            int n = static_cast<int>((dataSize - 4) / 24);
            const BYTE* clause = s + 4;
            sectLen = dataSize;
            for (int i = 0; i < n; ++i) {
                const BYTE* c = clause + i * 24;
                out.push_back(EHClause{rd32(c), rd32(c + 4), rd32(c + 8), rd32(c + 12), rd32(c + 16), rd32(c + 20)});
            }
        } else {
            std::uint32_t dataSize = s[1];
            int n = static_cast<int>((dataSize - 4) / 12);
            const BYTE* clause = s + 4;
            sectLen = dataSize;
            for (int i = 0; i < n; ++i) {
                const BYTE* c = clause + i * 12;
                out.push_back(EHClause{rd16(c), rd16(c + 2), c[4], rd16(c + 5), c[7], rd32(c + 8)});
            }
        }
        s += (sectLen + 3) & ~static_cast<std::size_t>(3);
    }
}

// ---- IL body decode (shared: both instrumenters decode a method body before rewriting it) ----

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

// A decoded method body header: where the IL code is and the bits needed to re-emit a fat method.
struct MethodHeader {
    const BYTE* code = nullptr; // start of the IL instruction stream
    std::uint32_t codeSize = 0;
    std::uint16_t maxStack = 0;
    std::uint32_t localSigTok = 0;
    bool initLocals = false;
    bool moreSects = false; // has trailing sections (EH table)
};

// Compressed-integer encode/decode (ECMA-335 II.23.2). uncompress advances p; returns false on malformed.
void compress(std::vector<BYTE>& b, std::uint32_t v);
bool uncompress(const BYTE*& p, const BYTE* end, std::uint32_t& out);

bool isRet(BYTE op);
bool isSwitch(BYTE op);
bool isShortBranch(BYTE op);
bool isLongBranch(BYTE op);
BYTE shortToLong(BYTE op); // short branch opcode -> its long (4-byte-operand) equivalent

// Parse a method body header (tiny or fat). Returns false on an unknown format we won't touch.
bool parseMethodHeader(const BYTE* p, MethodHeader& h);

// Decode the IL instruction stream into `insns`. Sets `unsafeToWrap` when the body contains a construct
// that can't live inside a try/finally (tail. / localloc / jmp). Returns false on malformed/truncated IL.
bool decodeBody(const BYTE* code, std::uint32_t codeSize, std::vector<Insn>& insns, bool& unsafeToWrap);

} // namespace Sherlock::il
