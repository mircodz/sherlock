#include "sherlock/profiler/il_writer.hpp"

namespace Sherlock::il {

// ECMA-335 IL opcode operand lengths. Returns operand byte count for a 1-byte opcode, or -1 if the opcode
// is unknown/unsupported (=> skip the method). Two-byte (0xFE-prefixed) opcodes are handled separately in
// the decoder. 0x45 (switch) is variable and flagged by the caller.
static int operandLen1(BYTE op) {
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
static int operandLen2(BYTE op2) {
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
// Map a short branch opcode to its long equivalent (all branches become 4-byte, avoiding iterative
// offset convergence).
BYTE shortToLong(BYTE op) {
    if (op == 0xDE) return 0xDD;         // leave.s -> leave
    return op + 0x0D;                    // br.s(0x2B)->br(0x38), cond .s -> long
}

void compress(std::vector<BYTE>& b, std::uint32_t v) {
    if (v <= 0x7F) { b.push_back(v); }
    else if (v <= 0x3FFF) { b.push_back(0x80 | (v >> 8)); b.push_back(v & 0xFF); }
    else { b.push_back(0xC0 | (v >> 24)); b.push_back((v >> 16) & 0xFF); b.push_back((v >> 8) & 0xFF); b.push_back(v & 0xFF); }
}

bool uncompress(const BYTE*& p, const BYTE* end, std::uint32_t& out) {
    if (p >= end) return false;
    BYTE b0 = *p;
    if ((b0 & 0x80) == 0) { out = b0; p += 1; return true; }
    if ((b0 & 0xC0) == 0x80) { if (p + 2 > end) return false; out = ((b0 & 0x3F) << 8) | p[1]; p += 2; return true; }
    if ((b0 & 0xE0) == 0xC0) { if (p + 4 > end) return false; out = ((b0 & 0x1F) << 24) | (p[1] << 16) | (p[2] << 8) | p[3]; p += 4; return true; }
    return false;
}

bool parseMethodHeader(const BYTE* p, MethodHeader& h) {
    BYTE fmt = p[0] & CorILMethod_FormatMask;
    if ((p[0] & 0x03) == CorILMethod_TinyFormat) {
        // Tiny format: low 2 bits == 0b10 (covers both TinyFormat and TinyFormat1 for odd code sizes);
        // size is the high 6 bits of the single header byte. maxStack is fixed at 8, no locals/sections.
        h.codeSize = p[0] >> 2; h.maxStack = 8; h.code = p + 1;
        return true;
    }
    if (fmt == CorILMethod_FatFormat) {
        std::uint16_t flags = rd16(p);
        std::uint16_t hdrDwords = flags >> 12;
        h.initLocals = (flags & CorILMethod_InitLocals) != 0;
        h.moreSects = (flags & CorILMethod_MoreSects) != 0;
        h.maxStack = rd16(p + 2);
        h.codeSize = rd32(p + 4);
        h.localSigTok = rd32(p + 8);
        h.code = p + hdrDwords * 4;
        return true;
    }
    return false; // unknown format, don't touch it
}

bool decodeBody(const BYTE* code, std::uint32_t codeSize, std::vector<Insn>& insns, bool& unsafeToWrap) {
    unsafeToWrap = false;
    std::uint32_t o = 0;
    while (o < codeSize) {
        Insn in{};
        in.off = o;
        in.raw = code + o;
        BYTE b = code[o];
        if (b == 0xFE) {
            if (o + 1 >= codeSize) return false;
            BYTE b2 = code[o + 1];
            int ol = operandLen2(b2);
            if (ol < 0) return false;
            // tail. (0x14) and localloc (0x0F): a tail call must be immediately followed by ret (our
            // ret->leave rewrite would break it); localloc requires an empty eval stack and can't live
            // inside a try. Leave such methods un-instrumented.
            if (b2 == 0x14 || b2 == 0x0F) unsafeToWrap = true;
            in.twoByte = true; in.op0 = 0xFE; in.op1 = b2; in.len = 2 + ol;
        } else if (isSwitch(b)) {
            if (o + 5 > codeSize) return false;
            std::uint32_t n = rd32(code + o + 1);
            std::uint32_t base = o + 5 + n * 4;
            if (base > codeSize) return false;
            in.op0 = b; in.sw = true; in.len = 5 + n * 4;
            for (std::uint32_t i = 0; i < n; ++i) {
                std::int32_t rel = static_cast<std::int32_t>(rd32(code + o + 5 + i * 4));
                in.swTargets.push_back(base + rel);
            }
        } else {
            int ol = operandLen1(b);
            if (ol < 0) return false;
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
        if (o + in.len > codeSize) return false;
        insns.push_back(in);
        o += in.len;
    }
    return true;
}

} // namespace Sherlock::il
