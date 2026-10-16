#include "sherlock/profiler/method_registry.hpp"
#include "sherlock/profiler/utf8.hpp"

#include <charconv>
#include <cstring>
#include <cstdint>
#include <format>
#include <map>
#include <optional>
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
struct EnumGuard {
    IMetaDataImport2* metadata;
    HCORENUM handle = nullptr;
    ~EnumGuard() {
        if (handle != nullptr) {
            metadata->CloseEnum(handle);
        }
    }
};

struct MetadataFailure {
    HRESULT status;
    std::string stage;
};

// Log lines escape what they quote; stored names stay as metadata spells them.
std::string printable(std::string_view text) {
    constexpr char hex[] = "0123456789abcdef";
    std::string result;
    result.reserve(text.size());
    for (unsigned char byte : text) {
        if (byte < 0x20 || byte == 0x7f) {
            result += "\\x";
            result += hex[byte >> 4];
            result += hex[byte & 0xf];
        } else {
            result += static_cast<char>(byte);
        }
    }
    return result;
}

void check(HRESULT status, std::string_view stage) {
    if (FAILED(status)) {
        throw MetadataFailure{status, std::string(stage)};
    }
}

// Method names must be valid UTF-16 without embedded NULs.
std::string utf8(const WCHAR* text, ULONG length, std::string_view stage) {
    std::span<const WCHAR> units(text, length);
    std::optional<std::string> converted = std::ranges::find(units, WCHAR{0}) == units.end() ? Sherlock::utf8(units) : std::nullopt;
    if (!converted) {
        throw MetadataFailure{E_INVALIDARG, std::string(stage) + "/invalid-utf16"};
    }
    return std::move(*converted);
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

// "Dictionary<TKey, TValue>+Entry" in namespace "System.Collections.Generic". typeParameters receives the
// innermost type's full generic parameter list, which VAR indices address.
std::string declaringType(IMetaDataImport2* metadata, mdTypeDef type, std::string& ns, std::vector<std::string>& typeParameters) {
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
        // Metadata spells an outermost type with its namespace and a nested one without.
        if (!IsTdNested(flags)) {
            std::size_t dot = part.rfind('.');
            ns = dot == std::string::npos ? std::string() : part.substr(0, dot);
            part = dot == std::string::npos ? part : part.substr(dot + 1);
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

// C# spells byref parameters by how they were declared, which metadata records beside the signature: out as a
// parameter flag, in and ref readonly returns as IsReadOnlyAttribute, ref readonly parameters as RequiresLocationAttribute.
std::string byrefKeyword(IMetaDataImport2* metadata, mdMethodDef method, std::uint32_t sequence) {
    mdParamDef param = mdParamDefNil;
    DWORD flags = 0;
    if (FAILED(metadata->GetParamForMethodIndex(method, sequence, &param)) || IsNilToken(param) ||
        FAILED(metadata->GetParamProps(param, nullptr, nullptr, nullptr, 0, nullptr, &flags, nullptr, nullptr, nullptr))) {
        return {};
    }
    auto has = [&](const WCHAR* attribute) {
        const void* data = nullptr;
        ULONG size = 0;
        return metadata->GetCustomAttributeByName(param, attribute, &data, &size) == S_OK;
    };
    if (IsPdOut(flags) && !IsPdIn(flags)) {
        return "out";
    }
    if (has(WCHAR("System.Runtime.CompilerServices.RequiresLocationAttribute"))) {
        return "ref readonly";
    }
    if (has(WCHAR("System.Runtime.CompilerServices.IsReadOnlyAttribute"))) {
        return sequence == 0 ? "ref readonly" : "in";
    }
    return {};
}

// The module's file name and version id, which with a method token identifies the method.
void describeModule(IMetaDataImport2* metadata, MethodName& name) {
    GUID mvid{};
    name.module = metadataName("GetScopeProps", [&](WCHAR* buffer, ULONG capacity, ULONG* length) {
        return metadata->GetScopeProps(buffer, capacity, length, &mvid);
    });
    static_assert(sizeof(mvid) == sizeof(name.mvid));
    std::memcpy(name.mvid.data(), &mvid, sizeof(mvid));
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

    MethodName name;
    try {
        describeModule(metadata.get(), name);
    } catch (const MetadataFailure& failure) {
        return {{}, failure.status, failure.stage};
    }
    // From here on a failure still knows the module, so the method keeps its identity.
    auto failed = [&](HRESULT status, std::string stage) {
        MethodName identity;
        identity.mvid = name.mvid;
        identity.module = std::move(name.module);
        return MethodRegistry::Resolution{std::move(identity), status, std::move(stage)};
    };
    try {
        mdTypeDef type = mdTypeDefNil;
        PCCOR_SIGNATURE blob = nullptr;
        ULONG length = 0;
        name.name = metadataName("GetMethodProps", [&](WCHAR* buffer, ULONG capacity, ULONG* written) {
            return metadata->GetMethodProps(token, &type, buffer, capacity, written, nullptr, &blob, &length, nullptr, nullptr);
        });
        // Metadata reports the parent of a global method, <Module>, as nil.
        if (IsNilToken(type)) {
            type = TokenFromRid(1, mdtTypeDef);
        }
        std::vector<std::string> methodParameters = genericParameterNames(metadata.get(), token);
        std::vector<std::string> typeParameters;
        name.type = declaringType(metadata.get(), type, name.ns, typeParameters);
        name.genericParameters = genericSuffix(methodParameters);

        signature::Context context{
            [&](mdToken referenced, bool qualified) { return typeName(metadata.get(), referenced, qualified); },
            [&](mdTypeSpec spec) {
                PCCOR_SIGNATURE data = nullptr;
                ULONG size = 0;
                check(metadata->GetTypeSpecFromToken(spec, &data, &size), "GetTypeSpecFromToken");
                return std::span<const BYTE>(data, size);
            },
            typeParameters,
            methodParameters,
            [&](std::uint32_t sequence) { return byrefKeyword(metadata.get(), token, sequence); },
        };
        std::optional<signature::Signature> brief = signature::describe({blob, length}, context, false);
        std::optional<signature::Signature> qualified = signature::describe({blob, length}, context, true);
        if (!brief || !qualified) {
            return failed(E_INVALIDARG, "signature");
        }
        name.parameters = std::move(brief->parameters);
        name.returnType = std::move(brief->returnType);
        name.qualifiedParameters = std::move(qualified->parameters);
        name.qualifiedReturnType = std::move(qualified->returnType);
        return {std::move(name), S_OK, {}};
    } catch (const MetadataFailure& failure) {
        return failed(failure.status, failure.stage);
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

std::string MethodRegistry::problem(const Entry& entry, HRESULT status, const std::string& stage) {
    return std::format("{} failed with HRESULT 0x{:08x} (module 0x{:x}, token 0x{:08x}, lifetime {})", stage,
                       static_cast<std::uint32_t>(status), static_cast<std::uint64_t>(entry.module),
                       static_cast<std::uint32_t>(entry.token), entry.generation);
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
            Entry entry{module, token, generation, {}, {}};
            entry.problem = problem(entry, E_PENDING, "resolution");
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
    if (SUCCEEDED(resolution.status) && resolution.name.name.empty()) {
        resolution.status = E_FAIL;
        resolution.stage = "resolver/empty-name";
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
            entry.problem = problem(entry, E_FAIL, "module-lifetime-ended-during-resolution");
            return frame;
        }
        if (SUCCEEDED(resolution.status)) {
            entry.name = std::move(resolution.name);
            entry.problem.clear();
            entry.resolved = true;
        } else {
            // An unresolved method keeps only its module's identity, never a partial name.
            entry.name = MethodName{};
            entry.name.mvid = resolution.name.mvid;
            entry.name.module = std::move(resolution.name.module);
            std::string failed = problem(entry, resolution.status, resolution.stage);
            if (failed != entry.problem) {
                warning = failed;
                entry.problem = std::move(failed);
            }
        }
    }
    if (logger_ != nullptr && !warning.empty()) {
        logger_->warn("unresolved method: {}", printable(warning));
    }
    return frame;
}

MethodRegistry::Method MethodRegistry::describe(FrameId frame) const {
    std::lock_guard lock(mutex_);
    auto found = entries_.find(frame);
    if (found == entries_.end()) {
        return {0, {}, std::format("frame {} was never registered", frame)};
    }
    const Entry& entry = found->second;
    return {entry.token, entry.name, entry.problem};
}

} // namespace Sherlock
