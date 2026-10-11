#include "sherlock/profiler/rejit_policy.hpp"

#include <algorithm>
#include <array>
#include <memory>
#include <string>
#include <utility>

namespace Sherlock::rejit {
namespace {

// MethodImplAttributes.Async (.NET 10 runtime async): the signature declares Task<T> but the IL returns T.
constexpr DWORD kRuntimeAsync = 0x2000;

// CoreLib methods the JIT calls as helpers, from the helper tables of .NET 9 and 10
// (src/coreclr/inc/jithelpers.h). Rewriting one can crash the runtime: .NET 9 asks for a helper's
// active native code, which a pending ReJIT leaves null. Their allocations belong to their callers anyway.
constexpr std::pair<std::string_view, std::string_view> kJitHelpers[] = {
    {"Internal.Runtime.CompilerHelpers.ThrowHelpers", "ThrowAmbiguousResolutionException"},
    {"Internal.Runtime.CompilerHelpers.ThrowHelpers", "ThrowArgumentException"},
    {"Internal.Runtime.CompilerHelpers.ThrowHelpers", "ThrowArgumentOutOfRangeException"},
    {"Internal.Runtime.CompilerHelpers.ThrowHelpers", "ThrowClassAccessException"},
    {"Internal.Runtime.CompilerHelpers.ThrowHelpers", "ThrowDivideByZeroException"},
    {"Internal.Runtime.CompilerHelpers.ThrowHelpers", "ThrowEntryPointNotFoundException"},
    {"Internal.Runtime.CompilerHelpers.ThrowHelpers", "ThrowFieldAccessException"},
    {"Internal.Runtime.CompilerHelpers.ThrowHelpers", "ThrowIndexOutOfRangeException"},
    {"Internal.Runtime.CompilerHelpers.ThrowHelpers", "ThrowMethodAccessException"},
    {"Internal.Runtime.CompilerHelpers.ThrowHelpers", "ThrowNotImplementedException"},
    {"Internal.Runtime.CompilerHelpers.ThrowHelpers", "ThrowNullReferenceException"},
    {"Internal.Runtime.CompilerHelpers.ThrowHelpers", "ThrowOverflowException"},
    {"Internal.Runtime.CompilerHelpers.ThrowHelpers", "ThrowPlatformNotSupportedException"},
    {"Internal.Runtime.CompilerHelpers.ThrowHelpers", "ThrowTypeNotSupportedException"},
    {"Internal.Runtime.CompilerHelpers.ThrowHelpers", "ThrowVerificationException"},
    {"System.Array", "CreateInstanceMDArray"},
    {"System.Buffer", "BulkMoveWithWriteBarrier"},
    {"System.Diagnostics.Debugger", "UserBreakpoint"},
    {"System.Math", "ConvertToInt32Checked"},
    {"System.Math", "ConvertToInt64Checked"},
    {"System.Math", "ConvertToUInt32Checked"},
    {"System.Math", "ConvertToUInt64Checked"},
    {"System.Math", "DivInt32"},
    {"System.Math", "DivInt64"},
    {"System.Math", "DivUInt32"},
    {"System.Math", "DivUInt64"},
    {"System.Math", "ModInt32"},
    {"System.Math", "ModInt64"},
    {"System.Math", "ModUInt32"},
    {"System.Math", "ModUInt64"},
    {"System.Math", "MultiplyChecked"},
    {"System.Runtime.CompilerServices.AsyncHelpers", "AllocContinuation"},
    {"System.Runtime.CompilerServices.AsyncHelpers", "AllocContinuationClass"},
    {"System.Runtime.CompilerServices.AsyncHelpers", "AllocContinuationMethod"},
    {"System.Runtime.CompilerServices.CastHelpers", "Box"},
    {"System.Runtime.CompilerServices.CastHelpers", "Box_Nullable"},
    {"System.Runtime.CompilerServices.CastHelpers", "ChkCastAny"},
    {"System.Runtime.CompilerServices.CastHelpers", "ChkCastClass"},
    {"System.Runtime.CompilerServices.CastHelpers", "ChkCastClassSpecial"},
    {"System.Runtime.CompilerServices.CastHelpers", "ChkCastInterface"},
    {"System.Runtime.CompilerServices.CastHelpers", "IsInstanceOfAny"},
    {"System.Runtime.CompilerServices.CastHelpers", "IsInstanceOfClass"},
    {"System.Runtime.CompilerServices.CastHelpers", "IsInstanceOfInterface"},
    {"System.Runtime.CompilerServices.CastHelpers", "Unbox"},
    {"System.Runtime.CompilerServices.CastHelpers", "Unbox_Nullable"},
    {"System.Runtime.CompilerServices.CastHelpers", "Unbox_TypeTest"},
    {"System.Runtime.CompilerServices.GenericsHelpers", "Class"},
    {"System.Runtime.CompilerServices.GenericsHelpers", "Method"},
    {"System.Runtime.CompilerServices.InitHelpers", "InitClass"},
    {"System.Runtime.CompilerServices.InitHelpers", "InitInstantiatedClass"},
    {"System.Runtime.CompilerServices.StaticsHelpers", "GetDynamicGCStaticBase"},
    {"System.Runtime.CompilerServices.StaticsHelpers", "GetDynamicGCThreadStaticBase"},
    {"System.Runtime.CompilerServices.StaticsHelpers", "GetDynamicNonGCStaticBase"},
    {"System.Runtime.CompilerServices.StaticsHelpers", "GetDynamicNonGCThreadStaticBase"},
    {"System.Runtime.CompilerServices.StaticsHelpers", "GetGCStaticBase"},
    {"System.Runtime.CompilerServices.StaticsHelpers", "GetGCThreadStaticBase"},
    {"System.Runtime.CompilerServices.StaticsHelpers", "GetNonGCStaticBase"},
    {"System.Runtime.CompilerServices.StaticsHelpers", "GetNonGCThreadStaticBase"},
    {"System.Runtime.CompilerServices.StaticsHelpers", "GetOptimizedGCThreadStaticBase"},
    {"System.Runtime.CompilerServices.StaticsHelpers", "GetOptimizedNonGCThreadStaticBase"},
    {"System.Runtime.CompilerServices.VirtualDispatchHelpers", "VirtualFunctionPointer"},
    {"System.RuntimeFieldHandle", "GetFieldAddr"},
    {"System.RuntimeFieldHandle", "GetStaticFieldAddr"},
    {"System.RuntimeFieldInfoStub", "FromPtr"},
    {"System.RuntimeMethodInfoStub", "FromPtr"},
    {"System.RuntimeTypeHandle", "GetRuntimeTypeFromHandle"},
    {"System.RuntimeTypeHandle", "GetRuntimeTypeFromHandleMaybeNull"},
    {"System.SpanHelpers", "ClearWithoutReferences"},
    {"System.SpanHelpers", "Fill"},
    {"System.SpanHelpers", "Memmove"},
    {"System.String", "StrCns"},
    {"System.Threading.Monitor", "Enter"},
    {"System.Threading.Monitor", "Exit"},
    {"System.Threading.Monitor", "ExitIfLockTaken"},
    {"System.Threading.Thread", "PollGC"},
    {"System.TypedReference", "GetRefAny"},
};

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

bool helperName(std::string_view method) {
    return std::ranges::any_of(kJitHelpers, [&](const auto& helper) { return helper.second == method; });
}

template <typename T>
struct Release {
    void operator()(T* value) const { value->Release(); }
};

} // namespace

const char* rejection(std::string_view assembly, std::string_view type, std::string_view method) {
    if (assembly != "System.Private.CoreLib") {
        return nullptr;
    }
    if (type == "System.Runtime.CompilerServices.CastHelpers" && (method == "StelemRef" || method == "LdelemaRef")) {
        return "CLR-owned array helper; ReJIT can invalidate its startup entry point";
    }
    if (std::ranges::find(kJitHelpers, std::pair{type, method}) != std::ranges::end(kJitHelpers)) {
        return "JIT helper; rewriting it can crash the runtime";
    }
    return nullptr;
}

const char* rejection(DWORD implementationFlags) {
    if ((implementationFlags & kRuntimeAsync) != 0) {
        return "runtime-async method; its IL returns a value its signature doesn't declare";
    }
    return nullptr;
}

const char* rejection(IMetaDataImport* metadata, mdMethodDef method) {
    if (metadata == nullptr) {
        return "method metadata is unavailable";
    }

    std::array<WCHAR, 64> methodName{};
    ULONG methodLength = 0;
    mdTypeDef type = mdTypeDefNil;
    DWORD implementationFlags = 0;
    if (FAILED(metadata->GetMethodProps(method, &type, methodName.data(), static_cast<ULONG>(methodName.size()),
                                       &methodLength, nullptr, nullptr, nullptr, nullptr, &implementationFlags))) {
        return "cannot read method metadata";
    }
    if (const char* reason = rejection(implementationFlags)) {
        return reason;
    }
    std::string name = asciiName(methodName, methodLength);
    if (name != "StelemRef" && name != "LdelemaRef" && !helperName(name)) {
        return nullptr;
    }

    std::array<WCHAR, 96> typeName{};
    ULONG typeLength = 0;
    if (FAILED(metadata->GetTypeDefProps(type, typeName.data(), static_cast<ULONG>(typeName.size()), &typeLength, nullptr, nullptr))) {
        return "cannot identify the helper's declaring type";
    }
    std::string declaringType = asciiName(typeName, typeLength);
    if (rejection("System.Private.CoreLib", declaringType, name) == nullptr) {
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
