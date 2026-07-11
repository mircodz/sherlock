#pragma once

#include "sherlock/storage/container.hpp"

#include <cstdint>
#include <span>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

// The interned symbol tables (Strings/Frames/Stacks/StackFrames). Frames dedup by name, stacks by
// frame-id sequence, so a stack is stored once and referenced by a 4-byte stackId.
namespace Sherlock::storage {

/// A frame: a slice of the Strings blob. frameId is the record's index in the Frames section.
struct FrameRecord {
    std::uint32_t strOffset;
    std::uint32_t strLen;
};
static_assert(sizeof(FrameRecord) == 8, "FrameRecord must be a packed 8-byte record");

/// A stack: a slice of the StackFrames pool. stackId is the record's index in the Stacks section.
struct StackRecord {
    std::uint32_t firstFrame;
    std::uint32_t frameCount;
};
static_assert(sizeof(StackRecord) == 8, "StackRecord must be a packed 8-byte record");

inline constexpr std::uint16_t kSymbolsVersion = 1;

/// Interns frames + stacks while capturing, then emits the four tables into a container. Frame
/// order is first-seen; a stack is keyed by the raw bytes of its frame-id sequence.
class StackInterner {
public:
    /// Returns the id for a frame name, assigning a new one on first sight.
    std::uint32_t internFrame(std::string_view name) {
        auto [it, inserted] =
            frameIds_.try_emplace(std::string(name), static_cast<std::uint32_t>(frameNames_.size()));
        if (inserted) {
            frameNames_.push_back(it->first);
        }
        return it->second;
    }

    /// Returns the id for a frame-id sequence (a stack), deduplicating identical stacks.
    std::uint32_t internStack(std::span<const std::uint32_t> frames) {
        std::string key(reinterpret_cast<const char*>(frames.data()), frames.size() * sizeof(std::uint32_t));
        auto [it, inserted] = stackIds_.try_emplace(std::move(key), static_cast<std::uint32_t>(stacks_.size()));
        if (inserted) {
            stacks_.emplace_back(frames.begin(), frames.end());
        }
        return it->second;
    }

    [[nodiscard]] std::size_t frameCount() const { return frameNames_.size(); }
    [[nodiscard]] std::size_t stackCount() const { return stacks_.size(); }

    /// Emits the Strings/Frames/Stacks/StackFrames sections into `w`.
    void writeTo(ContainerWriter& w) const {
        std::string strings;
        std::vector<FrameRecord> frames(frameNames_.size());
        for (std::size_t i = 0; i < frameNames_.size(); ++i) {
            frames[i] = {static_cast<std::uint32_t>(strings.size()),
                         static_cast<std::uint32_t>(frameNames_[i].size())};
            strings += frameNames_[i];
        }

        std::vector<StackRecord> stackRecs(stacks_.size());
        std::vector<std::uint32_t> pool;
        for (std::size_t i = 0; i < stacks_.size(); ++i) {
            stackRecs[i] = {static_cast<std::uint32_t>(pool.size()),
                            static_cast<std::uint32_t>(stacks_[i].size())};
            pool.insert(pool.end(), stacks_[i].begin(), stacks_[i].end());
        }

        const auto stringBytes =
            std::span<const std::byte>(reinterpret_cast<const std::byte*>(strings.data()), strings.size());
        w.addSection(SectionType::Strings, kSymbolsVersion, 0, stringBytes, strings.size());
        w.addRecords<FrameRecord>(SectionType::Frames, kSymbolsVersion, frames);
        w.addRecords<StackRecord>(SectionType::Stacks, kSymbolsVersion, stackRecs);
        w.addRecords<std::uint32_t>(SectionType::StackFrames, kSymbolsVersion, pool);
    }

private:
    std::unordered_map<std::string, std::uint32_t> frameIds_;
    std::vector<std::string_view> frameNames_; // views into frameIds_ keys (stable in a node map)
    std::unordered_map<std::string, std::uint32_t> stackIds_;
    std::vector<std::vector<std::uint32_t>> stacks_;
};

/// Read-only view over the interned tables in a parsed container. Borrows the container's bytes.
class StackTable {
public:
    static StackTable read(const ContainerReader& c) {
        StackTable t;
        if (auto s = c.find(SectionType::Strings)) {
            t.strings_ = s->data;
        }
        if (auto f = c.find(SectionType::Frames)) {
            t.frames_ = f->records<FrameRecord>();
        }
        if (auto s = c.find(SectionType::Stacks)) {
            t.stacks_ = s->records<StackRecord>();
        }
        if (auto p = c.find(SectionType::StackFrames)) {
            t.pool_ = p->records<std::uint32_t>();
        }
        return t;
    }

    [[nodiscard]] std::size_t frameCount() const { return frames_.size(); }
    [[nodiscard]] std::size_t stackCount() const { return stacks_.size(); }

    [[nodiscard]] std::string_view frame(std::uint32_t frameId) const {
        const FrameRecord& r = frames_[frameId];
        return {reinterpret_cast<const char*>(strings_.data()) + r.strOffset, r.strLen};
    }

    [[nodiscard]] std::span<const std::uint32_t> stackFrames(std::uint32_t stackId) const {
        const StackRecord& r = stacks_[stackId];
        return pool_.subspan(r.firstFrame, r.frameCount);
    }

private:
    std::span<const std::byte> strings_;
    std::span<const FrameRecord> frames_;
    std::span<const StackRecord> stacks_;
    std::span<const std::uint32_t> pool_;
};

} // namespace Sherlock::storage
