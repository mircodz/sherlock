#pragma once

#include "sherlock/storage/symbols.hpp"

#include <algorithm>
#include <cstdint>
#include <optional>
#include <span>
#include <string_view>
#include <vector>

// Allocation and per-object correlation records share one interned stack table.
namespace Sherlock::storage {

// One stack/type pair and its counters; naturally aligned to the C# 40-byte layout.
struct AllocationRecord {
    std::uint32_t stackId;
    std::uint32_t typeId;   // frameId (in the shared Frames/Strings table) of the allocated type name
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

// v2 replaces v1's reserved field with typeId in Frames/Strings.
// v1 has no type; the C# reader checks the section version before resolving typeId.
inline constexpr std::uint16_t kProfileVersion = 2;

// Emits allocation and correlation records with shared stacks. Callers resolve profiler-owned
// FrameIds through MethodRegistry and pass root -> leaf names; storage IDs are separate.
class ProvenanceWriter {
public:
    // Intern frame names, then their sequence.
    std::uint32_t internStack(std::span<const std::string_view> frames) {
        frameScratch_.clear();
        frameScratch_.reserve(frames.size());
        for (std::string_view f : frames) {
            frameScratch_.push_back(interner_.internFrame(f));
        }
        return interner_.internStack(frameScratch_);
    }

    // Type names share Frames/Strings with method names.
    std::uint32_t internType(std::string_view name) {
        return interner_.internFrame(name);
    }

    void addAllocation(std::uint32_t stackId, std::uint32_t typeId, std::uint64_t allocBytes,
                       std::uint64_t allocCount, std::uint64_t survivedBytes, std::uint64_t survivedCount) {
        allocs_.push_back({stackId, typeId, allocBytes, allocCount, survivedBytes, survivedCount});
    }

    void addObject(std::uint64_t address, std::uint32_t stackId) {
        corr_.push_back({address, stackId, 0});
    }

    [[nodiscard]] std::size_t allocationCount() const { return allocs_.size(); }
    [[nodiscard]] std::size_t objectCount() const { return corr_.size(); }

    // Terminal operation: sort correlation in place, then bound each chunk by chunkBytes.
    void writeTo(ContainerWriter& w, std::size_t chunkBytes = kDefaultChunkBytes) {
        interner_.writeTo(w);
        if (!allocs_.empty()) {
            w.addRecords<AllocationRecord>(SectionType::Allocations, kProfileVersion, allocs_);
        }
        if (!corr_.empty()) {
            // Sort globally before chunking so address lookups can cross chunk boundaries.
            std::sort(corr_.begin(), corr_.end(),
                      [](const CorrelationRecord& a, const CorrelationRecord& b) { return a.address < b.address; });
            w.addChunkedRecords<CorrelationRecord>(SectionType::Correlation, kProfileVersion, corr_, chunkBytes);
        }
    }

private:
    StackInterner interner_;
    std::vector<std::uint32_t> frameScratch_;
    std::vector<AllocationRecord> allocs_;
    std::vector<CorrelationRecord> corr_;
};

// Borrows allocation and stack bytes; owns the reassembled correlation column.
class ProvenanceReader {
public:
    explicit ProvenanceReader(const ContainerReader& c) : stacks_(StackTable::read(c)) {
        if (auto a = c.find(SectionType::Allocations)) {
            allocs_ = a->records<AllocationRecord>();
        }
        // Table-order chunks preserve global address order. The C# reader maps them in place.
        for (const SectionView& s : c.findAll(SectionType::Correlation)) {
            std::span<const CorrelationRecord> chunk = s.records<CorrelationRecord>();
            corr_.insert(corr_.end(), chunk.begin(), chunk.end());
        }
    }

    [[nodiscard]] const StackTable& stacks() const { return stacks_; }
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
    StackTable stacks_;
    std::span<const AllocationRecord> allocs_;
    std::vector<CorrelationRecord> corr_;
};

} // namespace Sherlock::storage
