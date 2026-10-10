#include "sherlock/profiler/method_registry.hpp"

#include <charconv>
#include <cstdint>
#include <format>
#include <map>
#include <memory>
#include <span>
#include <stdexcept>
#include <string_view>
#include <utility>
#include <vector>

#include "sherlock/common/logger.hpp"
#include "sherlock/profiler/signature.hpp"

namespace Sherlock {
namespace {

constexpr ULONG kMaxNameUnits = 1024 * 1024;
constexpr ULONG kMaxGenericParameters = 65536;
constexpr unsigned kMaxNesting = 128;
// Comparing labels is quadratic in the overload count; larger groups fall back to a token suffix.
constexpr std::size_t kMaxOverloads = 64;

struct EnumGuard {
    IMetaDataImport2* metadata;
    HCORENUM handle = nullptr;
    ~EnumGuard() {
        if (handle != nullptr) {
            metadata->CloseEnum(handle);
        }
    }
};

std::string safeLabel(std::string_view text) {
    constexpr char hex[] = "0123456789abcdef";
    std::string result;
    result.reserve(text.size());
    for (unsigned char byte : text) {
        if (byte < 0x20 || byte == 0x7f || byte == ';') {
            result += "\\x";
            result += hex[byte >> 4];
            result += hex[byte & 0xf];
        } else if (byte == '\\') {
            result += "\\\\";
        } else {
            result += static_cast<char>(byte);
        }
    }
    return result;
}

struct MetadataFailure {
    HRESULT status;
    std::string stage;
};

void check(HRESULT status, std::string_view stage) {
    if (FAILED(status)) {
        throw MetadataFailure{status, std::string(stage)};
    }
}

std::string utf8(const WCHAR* text, ULONG length, std::string_view stage) {
    std::string result;
    for (ULONG i = 0; i < length; ++i) {
        std::uint32_t value = text[i];
        if (value >= 0xd800 && value <= 0xdbff) {
            if (i + 1 >= length || text[i + 1] < 0xdc00 || text[i + 1] > 0xdfff) {
                throw MetadataFailure{E_INVALIDARG, std::string(stage) + "/invalid-utf16"};
            }
            value = 0x10000 + ((value - 0xd800) << 10) + (text[++i] - 0xdc00);
        } else if (value == 0 || value > 0xffff || (value >= 0xdc00 && value <= 0xdfff)) {
            throw MetadataFailure{E_INVALIDARG, std::string(stage) + "/invalid-utf16"};
        }

        if (value <= 0x7f) {
            result.push_back(static_cast<char>(value));
        } else if (value <= 0x7ff) {
            result.push_back(static_cast<char>(0xc0 | (value >> 6)));
            result.push_back(static_cast<char>(0x80 | (value & 0x3f)));
        } else if (value <= 0xffff) {
            result.push_back(static_cast<char>(0xe0 | (value >> 12)));
            result.push_back(static_cast<char>(0x80 | ((value >> 6) & 0x3f)));
            result.push_back(static_cast<char>(0x80 | (value & 0x3f)));
        } else {
            result.push_back(static_cast<char>(0xf0 | (value >> 18)));
            result.push_back(static_cast<char>(0x80 | ((value >> 12) & 0x3f)));
            result.push_back(static_cast<char>(0x80 | ((value >> 6) & 0x3f)));
            result.push_back(static_cast<char>(0x80 | (value & 0x3f)));
        }
    }
    return result;
}

template <typename Read>
std::string metadataName(std::string_view stage, Read read, bool allowEmpty = false) {
    ULONG needed = 0;
    check(read(nullptr, 0, &needed), stage);
    for (unsigned attempt = 0; attempt < 3; ++attempt) {
        if (allowEmpty && needed == 0) {
            return {};
        }
        if (needed < (allowEmpty ? 1u : 2u) || needed > kMaxNameUnits) {
            throw MetadataFailure{E_INVALIDARG, std::string(stage) + "/name-size"};
        }
        std::vector<WCHAR> buffer(needed);
        ULONG actual = 0;
        check(read(buffer.data(), static_cast<ULONG>(buffer.size()), &actual), stage);
        if (actual > buffer.size()) {
            needed = actual;
            continue;
        }
        if (allowEmpty && actual == 0) {
            return {};
        }
        if (actual < (allowEmpty ? 1u : 2u) || buffer[actual - 1] != 0) {
            throw MetadataFailure{E_INVALIDARG, std::string(stage) + "/name-termination"};
        }
        return utf8(buffer.data(), actual - 1, stage);
    }
    throw MetadataFailure{E_FAIL, std::string(stage) + "/name-changed"};
}

std::vector<std::string> genericParameterNames(IMetaDataImport2* metadata, mdToken owner) {
    EnumGuard enumeration{metadata};

    std::map<ULONG, std::string> parameters;
    std::size_t totalLength = 0;
    for (;;) {
        mdGenericParam tokens[32];
        ULONG count = 0;
        check(metadata->EnumGenericParams(&enumeration.handle, owner, tokens, 32, &count), "EnumGenericParams");
        if (count == 0) {
            break;
        }
        if (count > 32 || parameters.size() + count > kMaxGenericParameters) {
            throw MetadataFailure{E_INVALIDARG, "EnumGenericParams/count"};
        }
        for (ULONG i = 0; i < count; ++i) {
            ULONG sequence = 0;
            mdToken actualOwner = mdTokenNil;
            std::string parameter = metadataName("GetGenericParamProps", [&](WCHAR* buffer, ULONG capacity, ULONG* length) {
                return metadata->GetGenericParamProps(tokens[i], &sequence, nullptr, &actualOwner, nullptr, buffer, capacity, length);
            }, true);
            if (parameter.empty()) {
                parameter = std::format("{}{}", TypeFromToken(owner) == mdtMethodDef ? "M" : "T", sequence);
            }
            totalLength += parameter.size() + 1;
            if (actualOwner != owner || sequence >= kMaxGenericParameters || totalLength > kMaxNameUnits ||
                !parameters.emplace(sequence, std::move(parameter)).second) {
                throw MetadataFailure{E_INVALIDARG, "GetGenericParamProps/identity"};
            }
        }
    }

    std::vector<std::string> names;
    names.reserve(parameters.size());
    for (auto& [sequence, parameter] : parameters) {
        if (sequence != names.size()) {
            throw MetadataFailure{E_INVALIDARG, "GetGenericParamProps/sequence"};
        }
        names.push_back(std::move(parameter));
    }
    return names;
}

std::string genericSuffix(std::span<const std::string> names) {
    if (names.empty()) {
        return {};
    }
    std::string result = "<";
    for (std::size_t i = 0; i < names.size(); ++i) {
        if (i > 0) {
            result += ", ";
        }
        result += names[i];
    }
    return result + ">";
}

// typeParameters receives the innermost type's full generic parameter list, which VAR indices address.
std::string declaringType(IMetaDataImport2* metadata, mdTypeDef type, std::vector<std::string>& typeParameters) {
    std::string result;
    for (unsigned depth = 0; depth < kMaxNesting; ++depth) {
        if (TypeFromToken(type) != mdtTypeDef || IsNilToken(type)) {
            throw MetadataFailure{E_INVALIDARG, "GetTypeDefProps/token"};
        }
        DWORD flags = 0;
        std::string part = metadataName("GetTypeDefProps", [&](WCHAR* buffer, ULONG capacity, ULONG* length) {
            return metadata->GetTypeDefProps(type, buffer, capacity, length, &flags, nullptr);
        });
        std::vector<std::string> parameters = genericParameterNames(metadata, type);
        std::size_t ownArity = IsTdNested(flags) ? 0 : parameters.size();
        std::size_t marker = part.rfind('`');
        if (marker != std::string::npos && marker + 1 < part.size()) {
            ULONG arity = 0;
            const char* end = part.data() + part.size();
            auto parsed = std::from_chars(part.data() + marker + 1, end, arity);
            if (parsed.ec == std::errc{} && parsed.ptr == end) {
                if (arity > parameters.size()) {
                    throw MetadataFailure{E_INVALIDARG, "GetTypeDefProps/generic-arity"};
                }
                ownArity = arity;
                part.resize(marker);
            }
        }
        // Nested metadata repeats the enclosing type's parameters; the name's arity
        // counts only the trailing parameters introduced by this declaration.
        part += genericSuffix(std::span<const std::string>(parameters).last(ownArity));
        if (depth == 0) {
            typeParameters = std::move(parameters);
        }
        result = result.empty() ? std::move(part) : part + "+" + result;
        if (result.size() > kMaxNameUnits) {
            throw MetadataFailure{E_INVALIDARG, "GetTypeDefProps/symbol-size"};
        }
        if (!IsTdNested(flags)) {
            return result;
        }
        mdTypeDef enclosing = mdTypeDefNil;
        check(metadata->GetNestedClassProps(type, &enclosing), "GetNestedClassProps");
        type = enclosing;
    }
    throw MetadataFailure{E_INVALIDARG, "GetNestedClassProps/nesting-limit"};
}

// Short: "List". Qualified: "System.Collections.Generic.List", with enclosing types for nested types
// ("Api.Outer.Inner"). Generic arity markers are dropped; signatures supply the type arguments.
std::string typeName(IMetaDataImport2* metadata, mdToken type, bool qualified) {
    std::string result;
    for (unsigned depth = 0; depth < kMaxNesting; ++depth) {
        mdToken scope = mdTokenNil;
        DWORD flags = 0;
        std::string part;
        if (TypeFromToken(type) == mdtTypeDef) {
            part = metadataName("GetTypeDefProps", [&](WCHAR* buffer, ULONG capacity, ULONG* length) {
                return metadata->GetTypeDefProps(type, buffer, capacity, length, &flags, nullptr);
            });
            if (qualified && IsTdNested(flags)) {
                check(metadata->GetNestedClassProps(type, &scope), "GetNestedClassProps");
            }
        } else {
            part = metadataName("GetTypeRefProps", [&](WCHAR* buffer, ULONG capacity, ULONG* length) {
                return metadata->GetTypeRefProps(type, &scope, buffer, capacity, length);
            });
            if (TypeFromToken(scope) != mdtTypeRef) {
                scope = mdTokenNil; // resolved through a module or assembly, not an enclosing type
            }
        }
        part = qualified ? signature::withoutArity(part) : signature::shortTypeName(part);
        result = result.empty() ? std::move(part) : part + "." + result;
        if (!qualified || IsNilToken(scope)) {
            return result;
        }
        type = scope;
    }
    throw MetadataFailure{E_INVALIDARG, "typeName/nesting-limit"};
}

struct Overload {
    std::span<const BYTE> signature;
    std::vector<std::string> methodParameters;
};

// Formats parameter lists, as detailed as needed to keep every same-named method on the type distinct.
std::string parameterList(IMetaDataImport2* metadata, mdTypeDef type, mdMethodDef token, std::string_view name,
                          std::span<const std::string> typeParameters) {
    std::vector<mdMethodDef> tokens;
    {
        std::u16string wide;
        for (unsigned char byte : name) {
            if (byte >= 0x80) {
                wide.clear(); // non-ASCII names: list every method on the type and compare names below
                break;
            }
            wide.push_back(static_cast<char16_t>(byte));
        }
        EnumGuard enumeration{metadata};
        for (;;) {
            mdMethodDef batch[32];
            ULONG count = 0;
            HRESULT status = wide.empty()
                ? metadata->EnumMethods(&enumeration.handle, type, batch, 32, &count)
                : metadata->EnumMethodsWithName(&enumeration.handle, type, reinterpret_cast<LPCWSTR>(wide.c_str()), batch, 32, &count);
            check(status, "EnumMethodsWithName");
            if (count == 0) {
                break;
            }
            // A metadata table holds at most 2^24 rows; anything more is a broken enumerator.
            if (count > 32 || tokens.size() + count > 0x00ffffff) {
                throw MetadataFailure{E_INVALIDARG, "EnumMethodsWithName/count"};
            }
            tokens.insert(tokens.end(), batch, batch + count);
        }
    }

    std::vector<Overload> overloads;
    std::size_t self = SIZE_MAX;
    for (mdMethodDef candidate : tokens) {
        PCCOR_SIGNATURE blob = nullptr;
        ULONG length = 0;
        std::string candidateName = metadataName("GetMethodProps", [&](WCHAR* buffer, ULONG capacity, ULONG* written) {
            return metadata->GetMethodProps(candidate, nullptr, buffer, capacity, written, nullptr, &blob, &length, nullptr, nullptr);
        });
        if (candidateName != name) {
            continue;
        }
        if (candidate == token) {
            self = overloads.size();
        }
        overloads.push_back({{blob, length}, genericParameterNames(metadata, candidate)});
    }
    if (self == SIZE_MAX) {
        throw MetadataFailure{E_INVALIDARG, "EnumMethodsWithName/missing-self"};
    }

    auto label = [&](std::size_t index, signature::Detail detail) -> std::optional<std::string> {
        signature::Context context{
            [metadata](mdToken type, bool qualified) { return typeName(metadata, type, qualified); },
            [metadata](mdTypeSpec spec) {
                PCCOR_SIGNATURE data = nullptr;
                ULONG length = 0;
                check(metadata->GetTypeSpecFromToken(spec, &data, &length), "GetTypeSpecFromToken");
                return std::span<const BYTE>(data, length);
            },
            typeParameters,
            overloads[index].methodParameters,
        };
        try {
            return signature::format(overloads[index].signature, context, detail);
        } catch (const MetadataFailure&) {
            return std::nullopt;
        }
    };
    std::optional<std::string> distinct =
        overloads.size() <= kMaxOverloads ? signature::distinctLabel(self, overloads.size(), label) : std::nullopt;
    if (distinct) {
        return *distinct;
    }
    // Unformattable or indistinguishable (e.g. overloads differing only in custom modifiers).
    std::string fallback = label(self, {}).value_or("(?)");
    return std::format("{} [{:08x}]", fallback, static_cast<std::uint32_t>(token));
}

MethodRegistry::Resolution resolveDefinition(ICorProfilerInfo10* info, ModuleID module, mdMethodDef token) {
    if (info == nullptr) {
        return {{}, E_POINTER, "GetModuleMetaData/profiler"};
    }
    IMetaDataImport2* raw = nullptr;
    HRESULT status = info->GetModuleMetaData(module, ofRead, IID_IMetaDataImport2, reinterpret_cast<IUnknown**>(&raw));
    auto release = [](IMetaDataImport2* metadata) { metadata->Release(); };
    std::unique_ptr<IMetaDataImport2, decltype(release)> metadata(raw, release);
    if (FAILED(status) || metadata == nullptr) {
        return {{}, FAILED(status) ? status : E_POINTER, "GetModuleMetaData"};
    }

    try {
        mdTypeDef type = mdTypeDefNil;
        std::string method = metadataName("GetMethodProps", [&](WCHAR* buffer, ULONG capacity, ULONG* length) {
            return metadata->GetMethodProps(token, &type, buffer, capacity, length, nullptr, nullptr, nullptr, nullptr, nullptr);
        });
        std::vector<std::string> methodParameters = genericParameterNames(metadata.get(), token);
        std::vector<std::string> typeParameters;
        std::string symbol = declaringType(metadata.get(), type, typeParameters) + "." + method + genericSuffix(methodParameters);
        return {symbol + parameterList(metadata.get(), type, token, method, typeParameters), S_OK, {}};
    } catch (const MetadataFailure& failure) {
        return {{}, failure.status, failure.stage};
    }
}

} // namespace

MethodRegistry::MethodRegistry(ICorProfilerInfo10* info, Logger* logger)
    : MethodRegistry([info](ModuleID module, mdMethodDef token) { return resolveDefinition(info, module, token); }, logger) {}

MethodRegistry::MethodRegistry(Resolver resolver, Logger* logger)
    : resolver_(std::move(resolver)), logger_(logger) {
    if (!resolver_) {
        throw std::invalid_argument("MethodRegistry requires a resolver");
    }
}

void MethodRegistry::moduleLoaded(ModuleID module) {
    if (module == 0) {
        throw std::invalid_argument("MethodRegistry cannot register module 0");
    }
    std::lock_guard lock(mutex_);
    if (modules_.contains(module)) {
        return;
    }
    if (nextGeneration_ == 0) {
        throw std::overflow_error("MethodRegistry module generations exhausted");
    }
    modules_.emplace(module, Module{nextGeneration_++, {}});
}

void MethodRegistry::moduleUnloaded(ModuleID module) {
    std::lock_guard lock(mutex_);
    modules_.erase(module);
}

std::string MethodRegistry::identity(ModuleID module, mdMethodDef token, std::uint64_t generation) {
    return std::format("[module=0x{:x} token=0x{:08x} lifetime={}]", static_cast<std::uint64_t>(module),
                       static_cast<std::uint32_t>(token), generation);
}

std::string MethodRegistry::unresolved(FrameId frame, const Entry& entry, HRESULT status, const std::string& stage) {
    return std::format("<unresolved method frame={} {} stage={} HRESULT=0x{:08x}>", frame,
                       identity(entry.module, entry.token, entry.generation), safeLabel(stage), static_cast<std::uint32_t>(status));
}

FrameId MethodRegistry::intern(ModuleID module, mdMethodDef token) {
    if (TypeFromToken(token) != mdtMethodDef || IsNilToken(token)) {
        throw std::invalid_argument("MethodRegistry requires a non-nil MethodDef token");
    }

    FrameId frame;
    std::uint64_t generation;
    {
        std::lock_guard lock(mutex_);
        auto active = modules_.find(module);
        if (active == modules_.end()) {
            throw std::logic_error(std::format("MethodRegistry module 0x{:x} is not loaded", static_cast<std::uint64_t>(module)));
        }
        generation = active->second.generation;
        auto known = active->second.methods.find(token);
        if (known == active->second.methods.end()) {
            if (nextFrame_ == 0) {
                throw std::overflow_error("MethodRegistry frame IDs exhausted");
            }
            frame = nextFrame_++;
            Entry entry{module, token, generation, {}};
            entry.name = unresolved(frame, entry, E_PENDING, "resolution-pending");
            entries_.emplace(frame, std::move(entry));
            active->second.methods.emplace(token, frame);
        } else {
            frame = known->second;
        }
        Entry& entry = entries_.at(frame);
        if (entry.resolved || entry.resolving) {
            return frame;
        }
        entry.resolving = true;
    }

    Resolution resolution;
    try {
        // CLR resolution may reenter; never call it under mutex_.
        resolution = resolver_(module, token);
    } catch (...) {
        std::lock_guard lock(mutex_);
        entries_.at(frame).resolving = false;
        throw;
    }
    if (SUCCEEDED(resolution.status) && resolution.symbol.empty()) {
        resolution.status = E_FAIL;
        resolution.stage = "resolver/empty-symbol";
    }
    if (FAILED(resolution.status) && resolution.stage.empty()) {
        resolution.stage = "resolver";
    }

    std::string warning;
    {
        std::lock_guard lock(mutex_);
        Entry& entry = entries_.at(frame);
        entry.resolving = false;
        auto active = modules_.find(module);
        if (active == modules_.end() || active->second.generation != generation) {
            entry.name = unresolved(frame, entry, E_FAIL, "module-lifetime-ended-during-resolution");
            return frame;
        }
        if (SUCCEEDED(resolution.status)) {
            entry.name = safeLabel(resolution.symbol);
            entry.resolved = true;
        } else {
            std::string failed = unresolved(frame, entry, resolution.status, resolution.stage);
            if (failed != entry.name) {
                warning = failed;
                entry.name = std::move(failed);
            }
        }
    }
    if (logger_ != nullptr && !warning.empty()) {
        logger_->warn("{}", warning);
    }
    return frame;
}

std::string MethodRegistry::name(FrameId frame) const {
    std::lock_guard lock(mutex_);
    auto found = entries_.find(frame);
    if (found == entries_.end()) {
        return std::format("<unregistered method frame={}>", frame);
    }
    return found->second.name;
}

} // namespace Sherlock
