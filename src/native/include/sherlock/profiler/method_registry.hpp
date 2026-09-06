#pragma once

#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <unordered_map>

#include "profilercommon.h"

namespace Sherlock {

class Logger;
using FrameId = std::uint64_t;

class MethodRegistry {
public:
    struct Resolution {
        std::string symbol;
        HRESULT status = S_OK;
        std::string stage;
    };
    using Resolver = std::function<Resolution(ModuleID, mdMethodDef)>;

    MethodRegistry(ICorProfilerInfo10* info, Logger* logger);
    explicit MethodRegistry(Resolver resolver, Logger* logger = nullptr);

    void moduleLoaded(ModuleID module);
    void moduleUnloaded(ModuleID module);

    // Requires a live module registered by moduleLoaded. Retries unresolved definitions;
    // never called from allocation callbacks. Already-resolved symbols are immutable.
    FrameId intern(ModuleID module, mdMethodDef token);
    // Returns an owned copy with folded-stack separators and ASCII controls escaped.
    std::string name(FrameId frame) const;

private:
    struct Module {
        std::uint64_t generation;
        std::unordered_map<mdMethodDef, FrameId> methods;
    };
    struct Entry {
        ModuleID module;
        mdMethodDef token;
        std::uint64_t generation;
        std::string name;
        bool resolved = false;
        bool resolving = false;
    };

    static std::string identity(ModuleID module, mdMethodDef token, std::uint64_t generation);
    static std::string unresolved(FrameId frame, const Entry& entry, HRESULT status, const std::string& stage);

    Resolver resolver_;
    Logger* logger_;
    mutable std::mutex mutex_;
    std::unordered_map<ModuleID, Module> modules_;
    std::unordered_map<FrameId, Entry> entries_;
    FrameId nextFrame_ = 1;
    std::uint64_t nextGeneration_ = 1;
};

} // namespace Sherlock
