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

// The profile kind's version, covering every section; keep in sync with the C# ProfileFormat.Version.
inline constexpr std::uint32_t kProfileVersion = 2;

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

    // A live object and the stack that allocated it. Adding objects in address order spares a sort.
    void addObject(std::uint64_t address, std::uint32_t stackId) {
        addresses_.push_back(address);
        stacks_.push_back(stackId);
    }

    [[nodiscard]] std::size_t allocationCount() const { return allocs_.size(); }
    [[nodiscard]] std::size_t objectCount() const { return addresses_.size(); }

    // Terminal operation: hands the objects to the container, sorted by address so readers can binary-search
    // them. The container borrows the symbols and allocations, so this writer must outlive it.
    void writeTo(ContainerWriter& w) {
        symbols_.writeTo(w);
        if (!allocs_.empty()) {
            w.addRecords<AllocationRecord>(SectionType::Allocations, allocs_);
        }
        if (!addresses_.empty()) {
            sortObjects();
            w.addRecords(SectionType::ObjectAddresses, std::move(addresses_));
            w.addRecords(SectionType::ObjectStacks, std::move(stacks_));
        }
    }

private:
    void sortObjects() {
        if (std::is_sorted(addresses_.begin(), addresses_.end())) {
            return;
        }
        std::vector<std::size_t> order(addresses_.size());
        for (std::size_t i = 0; i < order.size(); ++i) {
            order[i] = i;
        }
        std::sort(order.begin(), order.end(), [this](std::size_t a, std::size_t b) { return addresses_[a] < addresses_[b]; });
        std::vector<std::uint64_t> addresses(order.size());
        std::vector<std::uint32_t> stacks(order.size());
        for (std::size_t i = 0; i < order.size(); ++i) {
            addresses[i] = addresses_[order[i]];
            stacks[i] = stacks_[order[i]];
        }
        addresses_ = std::move(addresses);
        stacks_ = std::move(stacks);
    }

    SymbolWriter symbols_;
    std::vector<AllocationRecord> allocs_;
    std::vector<std::uint64_t> addresses_;
    std::vector<std::uint32_t> stacks_;
};

// Borrows the container's bytes.
class ProvenanceReader {
public:
    explicit ProvenanceReader(const ContainerReader& c) : symbols_(SymbolTable::read(c)) {
        if (auto a = c.find(SectionType::Allocations)) {
            allocs_ = a->records<AllocationRecord>();
        }
        if (auto s = c.find(SectionType::ObjectAddresses)) {
            addresses_ = s->records<std::uint64_t>();
        }
        if (auto s = c.find(SectionType::ObjectStacks)) {
            stacks_ = s->records<std::uint32_t>();
        }
    }

    [[nodiscard]] const SymbolTable& symbols() const { return symbols_; }
    [[nodiscard]] std::span<const AllocationRecord> allocations() const { return allocs_; }
    [[nodiscard]] std::span<const std::uint64_t> objectAddresses() const { return addresses_; }
    [[nodiscard]] std::span<const std::uint32_t> objectStacks() const { return stacks_; }

    // Allocation stack ID, or nullopt for an untracked address.
    [[nodiscard]] std::optional<std::uint32_t> stackForAddress(std::uint64_t address) const {
        auto it = std::lower_bound(addresses_.begin(), addresses_.end(), address);
        if (it != addresses_.end() && *it == address) {
            return stacks_[static_cast<std::size_t>(it - addresses_.begin())];
        }
        return std::nullopt;
    }

private:
    SymbolTable symbols_;
    std::span<const AllocationRecord> allocs_;
    std::span<const std::uint64_t> addresses_;
    std::span<const std::uint32_t> stacks_;
};

} // namespace Sherlock::storage
