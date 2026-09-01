#include "sherlock/profiler/probe.hpp"

#include "sherlock/common/logger.hpp"

#include <algorithm>
#include <utility>
#include <vector>

namespace Sherlock {

namespace {

// Narrow ASCII std::string -> null-terminated WCHAR buffer (metadata names are ASCII; this
// sidesteps the L"" wchar_t-width mismatch on the PAL).
std::vector<WCHAR> widen(const std::string& s) {
    std::vector<WCHAR> w;
    w.reserve(s.size() + 1);
    for (char c : s)
        w.push_back(static_cast<WCHAR>(static_cast<unsigned char>(c)));
    w.push_back(0);
    return w;
}

} // namespace

extern "C" void Sherlock_ProbeEnter(std::intptr_t cookie) {
    ProbeRegistry::dispatch(static_cast<std::uintptr_t>(cookie), ProbePhase::Enter);
}

extern "C" void Sherlock_ProbeExit(std::intptr_t cookie) {
    ProbeRegistry::dispatch(static_cast<std::uintptr_t>(cookie), ProbePhase::Exit);
}

extern "C" void Sherlock_ProbeReturn(std::intptr_t cookie) {
    ProbeRegistry::dispatch(static_cast<std::uintptr_t>(cookie), ProbePhase::Return);
}

std::size_t ProbeRegistry::MethodKeyHash::operator()(const MethodKey& key) const noexcept {
    const std::size_t module = std::hash<std::uintptr_t>{}(static_cast<std::uintptr_t>(key.module));
    const std::size_t token = std::hash<std::uint32_t>{}(static_cast<std::uint32_t>(key.token));
    return module ^ (token + 0x9e3779b9u + (module << 6) + (module >> 2));
}

ProbeRegistry::Registration ProbeRegistry::registerMethod(
    ModuleID module, mdMethodDef token, std::string display, ProbeEvents events) {
    const MethodKey key{module, token};
    const std::uint8_t requested = static_cast<std::uint8_t>(events);

    std::lock_guard lock(mutex_);
    if (auto it = byMethod_.find(key); it != byMethod_.end()) {
        State* state = it->second;
        const std::uint8_t previous = state->events.load(std::memory_order_acquire);
        const ProbeEvents combined = static_cast<ProbeEvents>(previous | requested);
        const std::uint8_t fired = state->fired.load(std::memory_order_acquire);
        if ((fired & previous) == previous) {
            state->active.store(false, std::memory_order_release);
            auto replacement =
                std::make_unique<State>(this, module, token, std::move(display), combined);
            State* stable = replacement.get();
            states_.push_back(std::move(replacement));
            it->second = stable;
            return {{reinterpret_cast<std::uintptr_t>(stable), combined}, true, true};
        }

        state->events.store(static_cast<std::uint8_t>(combined), std::memory_order_release);
        return {{reinterpret_cast<std::uintptr_t>(state), combined}, combined != static_cast<ProbeEvents>(previous), false};
    }

    auto state = std::make_unique<State>(this, module, token, std::move(display), events);
    State* stable = state.get();
    states_.push_back(std::move(state));
    byMethod_.emplace(key, stable);
    return {{reinterpret_cast<std::uintptr_t>(stable), events}, true, true};
}

ProbePlan ProbeRegistry::planFor(ModuleID module, mdMethodDef token) const {
    std::lock_guard lock(mutex_);
    auto it = byMethod_.find(MethodKey{module, token});
    if (it == byMethod_.end() || !it->second->active.load(std::memory_order_acquire)) {
        return {};
    }

    State* state = it->second;
    return {
        reinterpret_cast<std::uintptr_t>(state),
        static_cast<ProbeEvents>(state->events.load(std::memory_order_acquire)),
    };
}

void ProbeRegistry::removeMethod(
    ModuleID module, mdMethodDef token, std::uintptr_t cookie) {
    std::lock_guard lock(mutex_);
    auto it = byMethod_.find(MethodKey{module, token});
    if (it == byMethod_.end() || reinterpret_cast<std::uintptr_t>(it->second) != cookie) {
        return;
    }
    it->second->active.store(false, std::memory_order_release);
    byMethod_.erase(it);
}

void ProbeRegistry::removeModule(ModuleID module) {
    std::lock_guard lock(mutex_);
    for (auto it = byMethod_.begin(); it != byMethod_.end();) {
        if (it->first.module == module) {
            it->second->active.store(false, std::memory_order_release);
            it = byMethod_.erase(it);
        } else {
            ++it;
        }
    }
}

void ProbeRegistry::dispatch(std::uintptr_t cookie, ProbePhase phase) noexcept {
    auto* state = reinterpret_cast<State*>(cookie);
    if (state == nullptr || !state->active.load(std::memory_order_acquire)) {
        return;
    }

    const std::uint8_t bit = static_cast<std::uint8_t>(phase);
    if ((state->events.load(std::memory_order_acquire) & bit) == 0 ||
        (state->fired.fetch_or(bit, std::memory_order_acq_rel) & bit) != 0) {
        return;
    }

    try {
        if (state->owner->onHit_) {
            state->owner->onHit_(state->display, phase);
        }
    } catch (...) {
        // Never allow user-facing event delivery to unwind through injected managed code.
    }
}

ProbeManager::ProbeManager(ICorProfilerInfo10* info, Logger* logger)
    : info_(info), logger_(logger) {}

bool ProbeManager::empty() const {
    std::lock_guard lock(mutex_);
    return specs_.empty();
}

void ProbeManager::configure(const std::string& spec, ProbeEvents events) {
    std::vector<Spec> parsed;
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
            if (logger_) {
                logger_->warn(
                    "ignoring malformed probe spec (want Ns.Type.Method): {}", item);
            }
            continue;
        }
        parsed.push_back({item.substr(0, dot), item.substr(dot + 1), events});
    }

    std::lock_guard lock(mutex_);
    for (Spec& candidate : parsed) {
        const bool duplicate = std::any_of(specs_.begin(), specs_.end(), [&](const Spec& existing) {
            return existing.type == candidate.type &&
                   existing.method == candidate.method &&
                   existing.events == candidate.events;
        });
        if (!duplicate) {
            specs_.push_back(std::move(candidate));
        }
    }
}

void ProbeManager::onModuleLoaded(ModuleID moduleId) {
    {
        std::lock_guard lock(mutex_);
        if (std::find(loadedModules_.begin(), loadedModules_.end(), moduleId) == loadedModules_.end()) {
            loadedModules_.push_back(moduleId);
        }
    }
    resolveInModule(moduleId, false);
}

void ProbeManager::onModuleUnloaded(ModuleID moduleId) {
    {
        std::lock_guard lock(mutex_);
        std::erase(loadedModules_, moduleId);
    }
    registry_.removeModule(moduleId);
}

bool ProbeManager::armLive(const std::string& spec, ProbeEvents events) {
    configure(spec, events);

    std::vector<ModuleID> modules;
    {
        std::lock_guard lock(mutex_);
        modules = loadedModules_;
    }

    std::size_t armed = 0;
    for (ModuleID module : modules) {
        armed += resolveInModule(module, true);
    }
    return armed > 0;
}

ProbePlan ProbeManager::registerMethod(
    ModuleID moduleId,
    mdMethodDef token,
    std::string display,
    ProbeEvents events) {
    return registry_.registerMethod(
        moduleId, token, std::move(display), events).plan;
}

std::size_t ProbeManager::resolveInModule(ModuleID moduleId, bool requestRejit) {
    std::vector<Spec> specs;
    {
        std::lock_guard lock(mutex_);
        specs = specs_;
    }
    if (specs.empty()) {
        return 0;
    }

    IMetaDataImport* md = nullptr;
    if (FAILED(info_->GetModuleMetaData(moduleId, ofRead, IID_IMetaDataImport, (IUnknown**)&md)) || md == nullptr) {
        return 0;
    }

    std::vector<ModuleID> reMods;
    std::vector<mdMethodDef> reToks;
    struct Added {
        ModuleID module;
        mdMethodDef token;
        std::uintptr_t cookie;
        bool inserted;
    };
    std::vector<Added> added;

    for (const Spec& s : specs) {
        std::vector<WCHAR> typeW = widen(s.type);
        mdTypeDef td = mdTypeDefNil;
        if (FAILED(md->FindTypeDefByName(typeW.data(), mdTokenNil, &td)) || td == mdTypeDefNil)
            continue;

        std::vector<WCHAR> methodW = widen(s.method);
        HCORENUM hEnum = nullptr;
        mdMethodDef methods[64];
        while (true) {
            ULONG count = 0;
            if (FAILED(md->EnumMethodsWithName(&hEnum, td, methodW.data(), methods, 64, &count))) {
                break;
            }
            for (ULONG i = 0; i < count; ++i) {
                ProbeRegistry::Registration registration = registry_.registerMethod(
                    moduleId, methods[i], s.type + "." + s.method, s.events);
                // A live arm deliberately retries an unchanged registration: RequestReJIT
                // succeeding does not guarantee that the later IL rewrite was accepted.
                if (!registration.changed && !requestRejit) {
                    continue;
                }
                if (std::find(reToks.begin(), reToks.end(), methods[i]) != reToks.end()) {
                    continue;
                }
                reMods.push_back(moduleId);
                reToks.push_back(methods[i]);
                added.push_back({moduleId, methods[i], registration.plan.cookie, registration.inserted});
            }
            if (count < 64) {
                break;
            }
        }
        if (hEnum != nullptr) {
            md->CloseEnum(hEnum);
        }
    }
    md->Release();

    if (reToks.empty() || !requestRejit) {
        return reToks.size();
    }

    HRESULT hr = info_->RequestReJIT(static_cast<ULONG>(reToks.size()), reMods.data(), reToks.data());
    if (FAILED(hr)) {
        for (const Added& entry : added) {
            if (entry.inserted) {
                registry_.removeMethod(entry.module, entry.token, entry.cookie);
            }
        }
        if (logger_) {
            logger_->error("RequestReJIT failed 0x{:08x}", static_cast<unsigned>(hr));
        }
        return 0;
    }
    if (logger_) {
        logger_->trace("armed {} method(s) for probing", reToks.size());
    }
    return reToks.size();
}

} // namespace Sherlock
