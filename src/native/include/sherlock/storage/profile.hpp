#pragma once

#include "sherlock/storage/symbols.hpp"

#include <algorithm>
#include <cstdint>
#include <optional>
#include <span>
#include <string_view>
#include <vector>

// The allocation-profile + correlation records, and the writer that ties them to the interned
// stack table. One shared table backs both the profile and per-object correlation (one identity
// space), so provenance is nearly free on top of the profile.
namespace Sherlock::storage {

/// One allocation site: a stack plus its allocated/survived byte+object counters. `reserved` is an
/// explicit pad so the record is a portable, naturally-aligned 40 bytes on both C++ and C#.
struct AllocationRecord {
    std::uint32_t stackId;
    std::uint32_t reserved;
    std::uint64_t allocBytes;
    std::uint64_t allocCount;
    std::uint64_t survivedBytes;
    std::uint64_t survivedCount;
};
static_assert(sizeof(AllocationRecord) == 40, "AllocationRecord must be a packed 40-byte record");

/// One live object's provenance: its heap address and the id of the stack that allocated it. 16
/// bytes, naturally aligned. Records are stored sorted by address so a lookup is a binary search.
struct CorrelationRecord {
    std::uint64_t address;
    std::uint32_t stackId;
    std::uint32_t reserved;
};
static_assert(sizeof(CorrelationRecord) == 16, "CorrelationRecord must be a packed 16-byte record");

inline constexpr std::uint16_t kProfileVersion = 1;

/// Accumulates an interned stack table plus allocation records (and, in the next step, correlation
/// records), then emits the whole container. Frames are given as names (root→leaf); the CLR-specific
/// FunctionID→name resolution stays in the caller so this codec is pure and testable.
class ProvenanceWriter {
public:
    /// Interns a stack (its frames, then the sequence) and returns its shared id.
    std::uint32_t internStack(std::span<const std::string_view> frames) {
        frameScratch_.clear();
        frameScratch_.reserve(frames.size());
        for (std::string_view f : frames) {
            frameScratch_.push_back(interner_.internFrame(f));
        }
        return interner_.internStack(frameScratch_);
    }

    void addAllocation(std::uint32_t stackId, std::uint64_t allocBytes, std::uint64_t allocCount,
                       std::uint64_t survivedBytes, std::uint64_t survivedCount) {
        allocs_.push_back({stackId, 0, allocBytes, allocCount, survivedBytes, survivedCount});
    }

    /// Records that the live object at `address` was allocated by `stackId` (shared with the profile).
    void addObject(std::uint64_t address, std::uint32_t stackId) {
        corr_.push_back({address, stackId, 0});
    }

    [[nodiscard]] std::size_t allocationCount() const { return allocs_.size(); }
    [[nodiscard]] std::size_t objectCount() const { return corr_.size(); }

    void writeTo(ContainerWriter& w) const {
        interner_.writeTo(w);
        if (!allocs_.empty()) {
            w.addRecords<AllocationRecord>(SectionType::Allocations, kProfileVersion, allocs_);
        }
        if (!corr_.empty()) {
            // Sort by address so the reader can binary-search; a Correlation section is emitted only
            // when there's provenance (the exit-time aggregate has none). (Parallel-sort at scale.)
            std::vector<CorrelationRecord> sorted(corr_);
            std::sort(sorted.begin(), sorted.end(),
                      [](const CorrelationRecord& a, const CorrelationRecord& b) { return a.address < b.address; });
            w.addRecords<CorrelationRecord>(SectionType::Correlation, kProfileVersion, sorted);
        }
    }

private:
    StackInterner interner_;
    std::vector<std::uint32_t> frameScratch_;
    std::vector<AllocationRecord> allocs_;
    std::vector<CorrelationRecord> corr_;
};

/// Read-only view over a provenance container: the allocation records plus the stack table needed
/// to resolve each record's `stackId` back to frame names.
class ProvenanceReader {
public:
    explicit ProvenanceReader(const ContainerReader& c) : stacks_(StackTable::read(c)) {
        if (auto a = c.find(SectionType::Allocations)) {
            allocs_ = a->records<AllocationRecord>();
        }
        if (auto co = c.find(SectionType::Correlation)) {
            corr_ = co->records<CorrelationRecord>();
        }
    }

    [[nodiscard]] const StackTable& stacks() const { return stacks_; }
    [[nodiscard]] std::span<const AllocationRecord> allocations() const { return allocs_; }
    [[nodiscard]] std::span<const CorrelationRecord> correlation() const { return corr_; }

    /// The id of the stack that allocated the object at `address`, or nullopt if untracked.
    /// Binary search over the address-sorted correlation records.
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
    std::span<const CorrelationRecord> corr_;
};

} // namespace Sherlock::storage
