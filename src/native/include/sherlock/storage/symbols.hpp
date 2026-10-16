#pragma once

#include "sherlock/storage/container.hpp"

#include <array>
#include <cstdint>
#include <cstring>
#include <map>
#include <span>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

// A profile's symbol tables. Methods are stored by identity (module MVID and metadata token), never by
// name: two methods that would be displayed alike stay separate records, and readers decide how to label
// them. Stacks are sequences of method indices, referenced by a 32-bit stackId. Names are stored as
// metadata spells them; readers escape them for display.
namespace Sherlock::storage {

using Mvid = std::array<std::uint8_t, 16>;

// A slice of Strings.
struct StringRef {
    std::uint32_t offset;
    std::uint32_t length;
};
static_assert(sizeof(StringRef) == 8, "StringRef must be a packed 8-byte record");

// moduleIndex is this record's index in Modules. An all-zero MVID means the module couldn't be read.
struct ModuleRecord {
    Mvid mvid;
    StringRef name; // file name, e.g. "System.Private.CoreLib.dll"
};
static_assert(sizeof(ModuleRecord) == 24, "ModuleRecord must be a packed 24-byte record");

// methodIndex is this record's index in Methods; StackFrames holds method indices.
struct MethodRecord {
    std::uint32_t module;            // index into Modules
    std::uint32_t token;             // MethodDef token; with the module's MVID, the method's identity
    StringRef ns;                    // "System.Collections.Generic"; empty for the global namespace
    StringRef type;                  // "Dictionary<TKey, TValue>+Entry": nested types joined by '+'
    StringRef name;                  // ".ctor", "Map", "System.IDisposable.Dispose"
    StringRef genericParameters;     // "<T>", or empty
    StringRef parameters;            // "(int, List<Order>)"
    StringRef qualifiedParameters;   // "(int, System.Collections.Generic.List<App.Order>)"
    StringRef returnType;            // "void"
    StringRef qualifiedReturnType;   // "System.Collections.Generic.List<App.Order>"
    StringRef problem;               // why the method couldn't be resolved; empty when it was
};
static_assert(sizeof(MethodRecord) == 80, "MethodRecord must be a packed 80-byte record");

// typeIndex is this record's index in Types: allocated type names, spelled as ClrMD names heap types.
struct TypeRecord {
    StringRef name;
};
static_assert(sizeof(TypeRecord) == 8, "TypeRecord must be a packed 8-byte record");

// StackFrames slice; stackId is this record's index in Stacks.
struct StackRecord {
    std::uint32_t firstFrame;
    std::uint32_t frameCount;
};
static_assert(sizeof(StackRecord) == 8, "StackRecord must be a packed 8-byte record");

// What a writer knows about one method. Views must stay valid until internMethod returns.
struct MethodDescription {
    Mvid mvid{};                     // all zero when unknown; the method then has no shared identity
    std::uint32_t token = 0;
    std::string_view module;
    std::string_view ns;
    std::string_view type;
    std::string_view name;
    std::string_view genericParameters;
    std::string_view parameters;
    std::string_view qualifiedParameters;
    std::string_view returnType;
    std::string_view qualifiedReturnType;
    std::string_view problem;
};

class SymbolWriter {
public:
    // Methods with a known MVID are deduplicated by (MVID, token). A method whose module couldn't be read
    // has no identity to compare, so every call adds a record; callers intern each of their methods once.
    std::uint32_t internMethod(const MethodDescription& m) {
        const bool known = m.mvid != Mvid{};
        if (known) {
            if (auto found = methodIds_.find({m.mvid, m.token}); found != methodIds_.end()) {
                // The same method seen through another load of its module: a name beats a problem.
                MethodRecord& existing = methods_[found->second];
                if (existing.problem.length != 0 && m.problem.empty()) {
                    existing = record(m);
                }
                return found->second;
            }
        }
        const auto id = static_cast<std::uint32_t>(methods_.size());
        methods_.push_back(record(m));
        if (known) {
            methodIds_.emplace(std::pair{m.mvid, m.token}, id);
        }
        return id;
    }

    std::uint32_t internType(std::string_view name) {
        auto [it, inserted] = typeIds_.try_emplace(std::string(name), static_cast<std::uint32_t>(types_.size()));
        if (inserted) {
            types_.push_back({internString(name)});
        }
        return it->second;
    }

    // Identical method-index sequences share one stackId.
    std::uint32_t internStack(std::span<const std::uint32_t> methods) {
        std::string key(reinterpret_cast<const char*>(methods.data()), methods.size() * sizeof(std::uint32_t));
        auto [it, inserted] = stackIds_.try_emplace(std::move(key), static_cast<std::uint32_t>(stacks_.size()));
        if (inserted) {
            stacks_.emplace_back(methods.begin(), methods.end());
        }
        return it->second;
    }

    [[nodiscard]] std::size_t methodCount() const { return methods_.size(); }
    [[nodiscard]] std::size_t typeCount() const { return types_.size(); }
    [[nodiscard]] std::size_t stackCount() const { return stacks_.size(); }

    void writeTo(ContainerWriter& w) const {
        std::vector<StackRecord> stackRecords(stacks_.size());
        std::vector<std::uint32_t> pool;
        for (std::size_t i = 0; i < stacks_.size(); ++i) {
            stackRecords[i] = {static_cast<std::uint32_t>(pool.size()), static_cast<std::uint32_t>(stacks_[i].size())};
            pool.insert(pool.end(), stacks_[i].begin(), stacks_[i].end());
        }
        const auto strings = std::span<const std::byte>(reinterpret_cast<const std::byte*>(strings_.data()), strings_.size());
        w.addBytes(SectionType::Strings, strings);
        w.addRecords<ModuleRecord>(SectionType::Modules, modules_);
        w.addRecords<MethodRecord>(SectionType::Methods, methods_);
        w.addRecords<TypeRecord>(SectionType::Types, types_);
        w.addRecords<StackRecord>(SectionType::Stacks, stackRecords);
        w.addRecords<std::uint32_t>(SectionType::StackFrames, pool);
    }

private:
    MethodRecord record(const MethodDescription& m) {
        return {internModule(m.mvid, m.module), m.token, internString(m.ns), internString(m.type), internString(m.name),
                internString(m.genericParameters), internString(m.parameters), internString(m.qualifiedParameters),
                internString(m.returnType), internString(m.qualifiedReturnType), internString(m.problem)};
    }

    StringRef internString(std::string_view text) {
        if (text.empty()) {
            return {0, 0};
        }
        auto [it, inserted] = stringIds_.try_emplace(std::string(text), StringRef{static_cast<std::uint32_t>(strings_.size()),
                                                                                  static_cast<std::uint32_t>(text.size())});
        if (inserted) {
            strings_ += text;
        }
        return it->second;
    }

    // One record per MVID; modules that couldn't be read share an all-zero record per name.
    std::uint32_t internModule(const Mvid& mvid, std::string_view name) {
        std::string key(reinterpret_cast<const char*>(mvid.data()), mvid.size());
        if (mvid == Mvid{}) {
            key += name;
        }
        auto [it, inserted] = moduleIds_.try_emplace(std::move(key), static_cast<std::uint32_t>(modules_.size()));
        if (inserted) {
            modules_.push_back({mvid, internString(name)});
        }
        return it->second;
    }

    std::string strings_;
    std::unordered_map<std::string, StringRef> stringIds_;
    std::vector<ModuleRecord> modules_;
    std::unordered_map<std::string, std::uint32_t> moduleIds_;
    std::vector<MethodRecord> methods_;
    std::map<std::pair<Mvid, std::uint32_t>, std::uint32_t> methodIds_;
    std::vector<TypeRecord> types_;
    std::unordered_map<std::string, std::uint32_t> typeIds_;
    std::unordered_map<std::string, std::uint32_t> stackIds_;
    std::vector<std::vector<std::uint32_t>> stacks_;
};

// Borrows the container's bytes. Readers outside tests are in C#; this one is for round-trip tests.
class SymbolTable {
public:
    static SymbolTable read(const ContainerReader& c) {
        SymbolTable t;
        if (auto s = c.find(SectionType::Strings)) {
            t.strings_ = s->data;
        }
        if (auto s = c.find(SectionType::Modules)) {
            t.modules_ = s->records<ModuleRecord>();
        }
        if (auto s = c.find(SectionType::Methods)) {
            t.methods_ = s->records<MethodRecord>();
        }
        if (auto s = c.find(SectionType::Types)) {
            t.types_ = s->records<TypeRecord>();
        }
        if (auto s = c.find(SectionType::Stacks)) {
            t.stacks_ = s->records<StackRecord>();
        }
        if (auto s = c.find(SectionType::StackFrames)) {
            t.pool_ = s->records<std::uint32_t>();
        }
        return t;
    }

    [[nodiscard]] std::size_t moduleCount() const { return modules_.size(); }
    [[nodiscard]] std::size_t methodCount() const { return methods_.size(); }
    [[nodiscard]] std::size_t typeCount() const { return types_.size(); }
    [[nodiscard]] std::size_t stackCount() const { return stacks_.size(); }
    [[nodiscard]] const ModuleRecord& module(std::uint32_t index) const { return modules_[index]; }
    [[nodiscard]] const MethodRecord& method(std::uint32_t index) const { return methods_[index]; }
    [[nodiscard]] std::string_view type(std::uint32_t index) const { return text(types_[index].name); }
    [[nodiscard]] std::string_view text(StringRef ref) const {
        return {reinterpret_cast<const char*>(strings_.data()) + ref.offset, ref.length};
    }
    [[nodiscard]] std::span<const std::uint32_t> stackFrames(std::uint32_t stackId) const {
        const StackRecord& r = stacks_[stackId];
        return pool_.subspan(r.firstFrame, r.frameCount);
    }

private:
    std::span<const std::byte> strings_;
    std::span<const ModuleRecord> modules_;
    std::span<const MethodRecord> methods_;
    std::span<const TypeRecord> types_;
    std::span<const StackRecord> stacks_;
    std::span<const std::uint32_t> pool_;
};

} // namespace Sherlock::storage
