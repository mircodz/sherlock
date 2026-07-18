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
    std::uint32_t typeId;   // frameId (in the shared Frames/Strings table) of the allocated type name
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

// v2 adds a real per-record type: `typeId` (was `reserved`) is the allocated type's id in the shared
// Frames/Strings table. v1 slabs have no type and read back with typeId == 0 (guarded on the reader).
inline constexpr std::uint16_t kProfileVersion = 2;

/// Accumulates an interned stack table plus allocation records (and, in the next step, correlation
/// records), then emits the whole container. Frames are given as names (root->leaf); the CLR-specific
/// FunctionID->name resolution stays in the caller so this codec is pure and testable.
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

    /// Interns a type name into the shared Frames/Strings table (types and method frames share one
    /// string space) and returns its id, stored on each allocation record as `typeId`.
    std::uint32_t internType(std::string_view name) {
        return interner_.internFrame(name);
    }

    void addAllocation(std::uint32_t stackId, std::uint32_t typeId, std::uint64_t allocBytes,
                       std::uint64_t allocCount, std::uint64_t survivedBytes, std::uint64_t survivedCount) {
        allocs_.push_back({stackId, typeId, allocBytes, allocCount, survivedBytes, survivedCount});
    }

    /// Records that the live object at `address` was allocated by `stackId` (shared with the profile).
    void addObject(std::uint64_t address, std::uint32_t stackId) {
        corr_.push_back({address, stackId, 0});
    }

    [[nodiscard]] std::size_t allocationCount() const { return allocs_.size(); }
    [[nodiscard]] std::size_t objectCount() const { return corr_.size(); }

    // Non-const: sorts corr_ in place (terminal operation). Avoids copying the (potentially
    // multi-GB) correlation column just to sort it. `chunkBytes` bounds each Correlation chunk section
    // (default 256 MiB; tests pass a tiny value to force the multi-chunk path).
    void writeTo(ContainerWriter& w, std::size_t chunkBytes = kDefaultChunkBytes) {
        interner_.writeTo(w);
        if (!allocs_.empty()) {
            w.addRecords<AllocationRecord>(SectionType::Allocations, kProfileVersion, allocs_);
        }
        if (!corr_.empty()) {
            // Sort by address so the reader can binary-search; a Correlation section is emitted only
            // when there's provenance (the exit-time aggregate has none). Chunked: one 16-byte record
            // per live object overflows a single section past ~134M objects.
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

/// Read-only view over a provenance container: the allocation records plus the stack table needed
/// to resolve each record's `stackId` back to frame names.
class ProvenanceReader {
public:
    explicit ProvenanceReader(const ContainerReader& c) : stacks_(StackTable::read(c)) {
        if (auto a = c.find(SectionType::Allocations)) {
            allocs_ = a->records<AllocationRecord>();
        }
        // Correlation may be split across chunk sections (addChunkedRecords). Concatenate them in
        // table order — the writer emits chunks in ascending global order, so the result stays
        // address-sorted. (C++ reader is test-only; the C# reader maps the chunks in place.)
        for (const SectionView& s : c.findAll(SectionType::Correlation)) {
            std::span<const CorrelationRecord> chunk = s.records<CorrelationRecord>();
            corr_.insert(corr_.end(), chunk.begin(), chunk.end());
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
    std::vector<CorrelationRecord> corr_;
};

} // namespace Sherlock::storage
