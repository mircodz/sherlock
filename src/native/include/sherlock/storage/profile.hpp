#pragma once

#include "sherlock/storage/symbols.hpp"

#include <algorithm>
#include <cstdint>
#include <optional>
#include <span>
#include <string_view>
#include <vector>

// Allocation and per-object correlation records share one symbol table (see symbols.hpp).
namespace Sherlock::storage {

// One stack/type pair and its counters; naturally aligned to the C# 40-byte layout.
struct AllocationRecord {
    std::uint32_t stackId;
    std::uint32_t typeId;   // index into Types: the allocated type
    std::uint64_t allocBytes;
    std::uint64_t allocCount;
    std::uint64_t survivedBytes;
    std::uint64_t survivedCount;
};
static_assert(sizeof(AllocationRecord) == 40, "AllocationRecord must be a packed 40-byte record");

// Address-sorted provenance records. Explicit padding preserves the C# 16-byte layout.
struct CorrelationRecord {
    std::uint64_t address;
    std::uint32_t stackId;
    std::uint32_t reserved;
};
static_assert(sizeof(CorrelationRecord) == 16, "CorrelationRecord must be a packed 16-byte record");

// The profile kind's version, covering every section; keep in sync with the C# ProfileFormat.Version.
inline constexpr std::uint32_t kProfileVersion = 1;

// Emits allocation and correlation records with shared stacks. Callers intern each method once
// (internMethod), then stacks of the returned indices, root -> leaf.
class ProvenanceWriter {
public:
    std::uint32_t internMethod(const MethodDescription& method) { return symbols_.internMethod(method); }
    std::uint32_t internStack(std::span<const std::uint32_t> methods) { return symbols_.internStack(methods); }
    std::uint32_t internType(std::string_view name) { return symbols_.internType(name); }

    void addAllocation(std::uint32_t stackId, std::uint32_t typeId, std::uint64_t allocBytes,
                       std::uint64_t allocCount, std::uint64_t survivedBytes, std::uint64_t survivedCount) {
        allocs_.push_back({stackId, typeId, allocBytes, allocCount, survivedBytes, survivedCount});
    }

    void addObject(std::uint64_t address, std::uint32_t stackId) {
        corr_.push_back({address, stackId, 0});
    }

    [[nodiscard]] std::size_t allocationCount() const { return allocs_.size(); }
    [[nodiscard]] std::size_t objectCount() const { return corr_.size(); }

    // Terminal operation: sorts correlation in place, so readers can binary-search it.
    void writeTo(ContainerWriter& w) {
        symbols_.writeTo(w);
        if (!allocs_.empty()) {
            w.addRecords<AllocationRecord>(SectionType::Allocations, allocs_);
        }
        if (!corr_.empty()) {
            std::sort(corr_.begin(), corr_.end(),
                      [](const CorrelationRecord& a, const CorrelationRecord& b) { return a.address < b.address; });
            w.addRecords<CorrelationRecord>(SectionType::Correlation, corr_);
        }
    }

private:
    SymbolWriter symbols_;
    std::vector<AllocationRecord> allocs_;
    std::vector<CorrelationRecord> corr_;
};

// Borrows the container's bytes.
class ProvenanceReader {
public:
    explicit ProvenanceReader(const ContainerReader& c) : symbols_(SymbolTable::read(c)) {
        if (auto a = c.find(SectionType::Allocations)) {
            allocs_ = a->records<AllocationRecord>();
        }
        if (auto s = c.find(SectionType::Correlation)) {
            corr_ = s->records<CorrelationRecord>();
        }
    }

    [[nodiscard]] const SymbolTable& symbols() const { return symbols_; }
    [[nodiscard]] std::span<const AllocationRecord> allocations() const { return allocs_; }
    [[nodiscard]] std::span<const CorrelationRecord> correlation() const { return corr_; }

    // Allocation stack ID, or nullopt for an untracked address.
    [[nodiscard]] std::optional<std::uint32_t> stackForAddress(std::uint64_t address) const {
        auto it = std::lower_bound(corr_.begin(), corr_.end(), address,
                                   [](const CorrelationRecord& r, std::uint64_t a) { return r.address < a; });
        if (it != corr_.end() && it->address == address) {
            return it->stackId;
        }
        return std::nullopt;
    }

private:
    SymbolTable symbols_;
    std::span<const AllocationRecord> allocs_;
    std::span<const CorrelationRecord> corr_;
};

} // namespace Sherlock::storage
