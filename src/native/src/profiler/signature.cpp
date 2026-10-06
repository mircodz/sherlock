#include "sherlock/profiler/signature.hpp"

#include <cstdint>

#include "sherlock/profiler/il_writer.hpp"

namespace Sherlock::signature {
namespace {

constexpr unsigned kMaxDepth = 64;
constexpr std::uint32_t kMaxRank = 32;
// Bounds the output (and the work) when TypeSpecs fan out into each other.
constexpr std::size_t kMaxLength = 64 * 1024;

struct Reader {
    const BYTE* p;
    const BYTE* end;

    bool byte(BYTE& value) {
        if (p >= end) {
            return false;
        }
        value = *p++;
        return true;
    }

    bool number(std::uint32_t& value) { return il::uncompress(p, end, value); }
};

const char* keyword(BYTE element) {
    switch (element) {
        case ELEMENT_TYPE_VOID: return "void";
        case ELEMENT_TYPE_BOOLEAN: return "bool";
        case ELEMENT_TYPE_CHAR: return "char";
        case ELEMENT_TYPE_I1: return "sbyte";
        case ELEMENT_TYPE_U1: return "byte";
        case ELEMENT_TYPE_I2: return "short";
        case ELEMENT_TYPE_U2: return "ushort";
        case ELEMENT_TYPE_I4: return "int";
        case ELEMENT_TYPE_U4: return "uint";
        case ELEMENT_TYPE_I8: return "long";
        case ELEMENT_TYPE_U8: return "ulong";
        case ELEMENT_TYPE_R4: return "float";
        case ELEMENT_TYPE_R8: return "double";
        case ELEMENT_TYPE_I: return "nint";
        case ELEMENT_TYPE_U: return "nuint";
        case ELEMENT_TYPE_STRING: return "string";
        case ELEMENT_TYPE_OBJECT: return "object";
        case ELEMENT_TYPE_TYPEDBYREF: return "TypedReference";
        default: return nullptr;
    }
}

class Formatter {
public:
    explicit Formatter(const Context& context) : context_(context) {}

    bool method(Reader& reader, std::string& parameters, std::string& returnType, unsigned depth) {
        BYTE convention = 0;
        std::uint32_t count = 0;
        if (!reader.byte(convention)) {
            return false;
        }
        const BYTE kind = convention & IMAGE_CEE_CS_CALLCONV_MASK;
        if (kind == IMAGE_CEE_CS_CALLCONV_FIELD || kind == IMAGE_CEE_CS_CALLCONV_LOCAL_SIG ||
            kind == IMAGE_CEE_CS_CALLCONV_PROPERTY || kind == IMAGE_CEE_CS_CALLCONV_GENERICINST) {
            return false;
        }
        if ((convention & IMAGE_CEE_CS_CALLCONV_GENERIC) != 0 && !reader.number(count)) {
            return false;
        }
        if (!reader.number(count) || !type(reader, returnType, depth + 1)) {
            return false;
        }
        for (std::uint32_t i = 0; i < count; ++i) {
            if (reader.p < reader.end && *reader.p == ELEMENT_TYPE_SENTINEL) {
                ++reader.p; // call-site vararg marker inside a function pointer
            }
            if ((i > 0 && !append(parameters, ", ")) || !type(reader, parameters, depth + 1)) {
                return false;
            }
        }
        return kind != IMAGE_CEE_CS_CALLCONV_VARARG || append(parameters, count == 0 ? "__arglist" : ", __arglist");
    }

private:
    bool append(std::string& out, std::string_view text) {
        if (text.size() > budget_) {
            return false;
        }
        budget_ -= text.size();
        out += text;
        return true;
    }

    bool type(Reader& reader, std::string& out, unsigned depth) {
        BYTE element = 0;
        if (depth > kMaxDepth || !reader.byte(element)) {
            return false;
        }
        if (const char* name = keyword(element)) {
            return append(out, name);
        }
        switch (element) {
            case ELEMENT_TYPE_CLASS:
            case ELEMENT_TYPE_VALUETYPE:
                return token(reader, out, depth);
            case ELEMENT_TYPE_PTR:
                return type(reader, out, depth + 1) && append(out, "*");
            case ELEMENT_TYPE_BYREF:
                return append(out, "ref ") && type(reader, out, depth + 1);
            case ELEMENT_TYPE_SZARRAY:
                return type(reader, out, depth + 1) && append(out, "[]");
            case ELEMENT_TYPE_ARRAY: {
                std::uint32_t rank = 0;
                if (!type(reader, out, depth + 1) || !reader.number(rank) || rank == 0 || rank > kMaxRank) {
                    return false;
                }
                for (int list = 0; list < 2; ++list) { // sizes, then lower bounds
                    std::uint32_t count = 0;
                    std::uint32_t ignored = 0;
                    if (!reader.number(count)) {
                        return false;
                    }
                    for (std::uint32_t i = 0; i < count; ++i) {
                        if (!reader.number(ignored)) {
                            return false;
                        }
                    }
                }
                return append(out, rank == 1 ? "[*]" : "[" + std::string(rank - 1, ',') + "]");
            }
            case ELEMENT_TYPE_GENERICINST: {
                BYTE kind = 0;
                std::uint32_t count = 0;
                if (!reader.byte(kind) || (kind != ELEMENT_TYPE_CLASS && kind != ELEMENT_TYPE_VALUETYPE) ||
                    !token(reader, out, depth) || !reader.number(count) || count == 0 || !append(out, "<")) {
                    return false;
                }
                for (std::uint32_t i = 0; i < count; ++i) {
                    if ((i > 0 && !append(out, ", ")) || !type(reader, out, depth + 1)) {
                        return false;
                    }
                }
                return append(out, ">");
            }
            case ELEMENT_TYPE_VAR:
            case ELEMENT_TYPE_MVAR: {
                const std::span<const std::string> names =
                    element == ELEMENT_TYPE_VAR ? context_.typeParameters : context_.methodParameters;
                std::uint32_t index = 0;
                return reader.number(index) && index < names.size() && append(out, names[index]);
            }
            case ELEMENT_TYPE_FNPTR: {
                std::string parameters;
                std::string returnType;
                return method(reader, parameters, returnType, depth + 1) && append(out, "delegate*<") &&
                       append(out, parameters) && (parameters.empty() || append(out, ", ")) &&
                       append(out, returnType) && append(out, ">");
            }
            case ELEMENT_TYPE_CMOD_REQD:
            case ELEMENT_TYPE_CMOD_OPT: {
                std::uint32_t modifier = 0;
                return reader.number(modifier) && type(reader, out, depth + 1);
            }
            default:
                return false;
        }
    }

    // TypeDefOrRefOrSpecEncoded (ECMA-335 II.23.2.8).
    bool token(Reader& reader, std::string& out, unsigned depth) {
        static constexpr mdToken kTables[] = {mdtTypeDef, mdtTypeRef, mdtTypeSpec};
        std::uint32_t coded = 0;
        if (!reader.number(coded) || (coded & 3) == 3 || (coded >> 2) == 0 || (coded >> 2) > 0x00ffffff) {
            return false;
        }
        const mdToken token = kTables[coded & 3] | (coded >> 2);
        if (TypeFromToken(token) != mdtTypeSpec) {
            return append(out, shortTypeName(context_.typeName(token)));
        }
        const std::span<const BYTE> blob = context_.typeSpec(token);
        Reader spec{blob.data(), blob.data() + blob.size()};
        return type(spec, out, depth + 1);
    }

    const Context& context_;
    std::size_t budget_ = kMaxLength;
};

} // namespace

std::string shortTypeName(std::string_view metadataName) {
    std::size_t dot = metadataName.rfind('.');
    if (dot != std::string_view::npos && dot + 1 < metadataName.size()) {
        metadataName.remove_prefix(dot + 1);
    }
    std::size_t marker = metadataName.rfind('`');
    if (marker != std::string_view::npos && marker > 0 && marker + 1 < metadataName.size() &&
        metadataName.find_first_not_of("0123456789", marker + 1) == std::string_view::npos) {
        metadataName = metadataName.substr(0, marker);
    }
    return std::string(metadataName);
}

std::optional<std::string> format(std::span<const BYTE> methodSignature, const Context& context, bool includeReturnType) {
    Reader reader{methodSignature.data(), methodSignature.data() + methodSignature.size()};
    Formatter formatter(context);
    std::string parameters;
    std::string returnType;
    if (!formatter.method(reader, parameters, returnType, 0)) {
        return std::nullopt;
    }
    std::string result = "(" + parameters + ")";
    if (includeReturnType) {
        result += ":" + returnType;
    }
    return result;
}

} // namespace Sherlock::signature
