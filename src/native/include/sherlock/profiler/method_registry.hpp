#pragma once

#include <array>
#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <unordered_map>

#include "profilercommon.h"

namespace Sherlock {

class Logger;
using FrameId = std::uint64_t;

// A method's name, in parts, as its metadata spells them (UTF-8, not escaped). Readers decide how to
// display it; nothing here is meant to be unique on its own.
struct MethodName {
    std::array<std::uint8_t, 16> mvid{}; // the module's version id; with the token, the method's identity
    std::string module;                  // module file name, e.g. "System.Private.CoreLib.dll"
    std::string ns;                      // "System.Collections.Generic"; empty for the global namespace
    std::string type;                    // "Dictionary<TKey, TValue>+Entry"
    std::string name;                    // "Add", ".ctor", "System.IDisposable.Dispose"
    std::string genericParameters;       // "<T>", or empty
    std::string parameters;              // "(int, List<Order>)"
    std::string qualifiedParameters;     // "(int, System.Collections.Generic.List<App.Order>)"
    std::string returnType;
    std::string qualifiedReturnType;
};

class MethodRegistry {
public:
    struct Resolution {
        MethodName name;
        HRESULT status = S_OK;
        std::string stage;
    };
    using Resolver = std::function<Resolution(ModuleID, mdMethodDef)>;

    // What the registry knows about a frame's method.
    struct Method {
        mdMethodDef token = 0;
        MethodName name;     // when unresolved, only the module's MVID and name, if it could be read
        std::string problem; // why the method isn't resolved; empty when it is
    };

    MethodRegistry(ICorProfilerInfo10* info, Logger* logger);
    explicit MethodRegistry(Resolver resolver, Logger* logger = nullptr);

    // Repeated loads are idempotent; unload/reload starts a new lifetime without reusing FrameIds.
    void moduleLoaded(ModuleID module);
    void moduleUnloaded(ModuleID module);

    // Requires a live module registered by moduleLoaded. Retries unresolved definitions;
    // never called from allocation callbacks. Already-resolved methods are immutable.
    FrameId intern(ModuleID module, mdMethodDef token);
    // Returns an owned copy; a frame stays describable after its module unloads.
    Method describe(FrameId frame) const;

private:
    struct Module {
        std::uint64_t generation;
        std::unordered_map<mdMethodDef, FrameId> methods;
    };
    struct Entry {
        ModuleID module;
        mdMethodDef token;
        std::uint64_t generation;
        MethodName name;
        std::string problem;
        bool resolved = false;
        bool resolving = false;
    };

    static std::string problem(const Entry& entry, HRESULT status, const std::string& stage);

    Resolver resolver_;
    Logger* logger_;
    mutable std::mutex mutex_;
    std::unordered_map<ModuleID, Module> modules_;
    std::unordered_map<FrameId, Entry> entries_;
    FrameId nextFrame_ = 1;
    std::uint64_t nextGeneration_ = 1;
};

} // namespace Sherlock
