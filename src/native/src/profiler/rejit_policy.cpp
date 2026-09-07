#include "sherlock/profiler/rejit_policy.hpp"

#include <array>
#include <memory>
#include <string>

namespace Sherlock::rejit {
namespace {

template <std::size_t Size>
std::string asciiName(const std::array<WCHAR, Size>& buffer, ULONG length) {
    // Longer or non-ASCII names cannot match the fixed CLR identities.
    if (length == 0 || length > Size || buffer[length - 1] != 0) {
        return {};
    }
    std::string name;
    name.reserve(length - 1);
    for (ULONG i = 0; i + 1 < length; ++i) {
        if (buffer[i] == 0 || buffer[i] > 127) {
            return {};
        }
        name.push_back(static_cast<char>(buffer[i]));
    }
    return name;
}

template <typename T>
struct Release {
    void operator()(T* value) const { value->Release(); }
};

} // namespace

const char* rejection(std::string_view assembly, std::string_view type, std::string_view method) {
    if (assembly == "System.Private.CoreLib" && type == "System.Runtime.CompilerServices.CastHelpers" && (method == "StelemRef" || method == "LdelemaRef")) {
        return "CLR-owned array helper; ReJIT can invalidate its startup entry point";
    }
    return nullptr;
}

const char* rejection(IMetaDataImport* metadata, mdMethodDef method) {
    if (metadata == nullptr) {
        return "method metadata is unavailable";
    }

    std::array<WCHAR, 32> methodName{};
    ULONG methodLength = 0;
    mdTypeDef type = mdTypeDefNil;
    if (FAILED(metadata->GetMethodProps(method, &type, methodName.data(), static_cast<ULONG>(methodName.size()),
                                       &methodLength, nullptr, nullptr, nullptr, nullptr, nullptr))) {
        return "cannot read method metadata";
    }
    std::string name = asciiName(methodName, methodLength);
    if (name != "StelemRef" && name != "LdelemaRef") {
        return nullptr;
    }

    std::array<WCHAR, 96> typeName{};
    ULONG typeLength = 0;
    if (FAILED(metadata->GetTypeDefProps(type, typeName.data(), static_cast<ULONG>(typeName.size()), &typeLength, nullptr, nullptr))) {
        return "cannot identify the helper's declaring type";
    }
    std::string declaringType = asciiName(typeName, typeLength);
    if (declaringType != "System.Runtime.CompilerServices.CastHelpers") {
        return nullptr;
    }

    IMetaDataAssemblyImport* raw = nullptr;
    HRESULT status = metadata->QueryInterface(IID_IMetaDataAssemblyImport, reinterpret_cast<void**>(&raw));
    std::unique_ptr<IMetaDataAssemblyImport, Release<IMetaDataAssemblyImport>> assemblyMetadata(raw);
    if (FAILED(status) || !assemblyMetadata) {
        return "cannot identify the helper's assembly";
    }
    mdAssembly assembly = mdAssemblyNil;
    if (FAILED(assemblyMetadata->GetAssemblyFromScope(&assembly))) {
        return "cannot identify the helper's assembly";
    }
    std::array<WCHAR, 64> assemblyName{};
    ULONG assemblyLength = 0;
    if (FAILED(assemblyMetadata->GetAssemblyProps(assembly, nullptr, nullptr, nullptr, assemblyName.data(),
                                                 static_cast<ULONG>(assemblyName.size()), &assemblyLength, nullptr, nullptr))) {
        return "cannot read the helper's assembly name";
    }
    return rejection(asciiName(assemblyName, assemblyLength), declaringType, name);
}

const char* rejection(ICorProfilerInfo10* info, ModuleID module, mdMethodDef method) {
    if (info == nullptr) {
        return "profiler metadata access is unavailable";
    }
    IMetaDataImport* raw = nullptr;
    HRESULT status = info->GetModuleMetaData(module, ofRead, IID_IMetaDataImport, reinterpret_cast<IUnknown**>(&raw));
    std::unique_ptr<IMetaDataImport, Release<IMetaDataImport>> metadata(raw);
    if (FAILED(status) || !metadata) {
        return "module metadata is unavailable";
    }
    return rejection(metadata.get(), method);
}

} // namespace Sherlock::rejit
