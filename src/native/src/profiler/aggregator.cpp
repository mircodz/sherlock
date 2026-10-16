#include "sherlock/profiler/aggregator.hpp"
#include "sherlock/profiler/utf8.hpp"

#include "sherlock/common/logger.hpp"
#include "sherlock/profiler/signature.hpp"
#include "sherlock/storage/profile.hpp"

#include <algorithm>
#include <cassert>
#include <cerrno>
#include <cstdio>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <limits>
#include <memory>
#include <mutex>
#include <optional>
#include <system_error>
#include <string_view>
#include <unordered_map>
#include <utility>
#include <span>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#else
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

namespace Sherlock {

namespace {

// Use a monotonic owner ID, not this: a new Aggregator may reuse a destroyed one's address.
struct ThreadShard {
    std::uint64_t ownerId = 0;
    Aggregator::Shard* shard = nullptr;
};
thread_local ThreadShard t_shard;

// Set once the thread's exit handler ran: a later allocation on the exiting thread records into the overflow shard,
// which needs no exit handler.
thread_local bool t_exited = false;

std::atomic<std::uint64_t> g_nextAggregatorId{1};

// Live aggregators by owner ID, so a thread that exits after its aggregator was destroyed doesn't touch it. Never
// destroyed: threads may exit while static objects are being destroyed.
struct Owners {
    std::mutex mutex;
    std::unordered_map<std::uint64_t, Aggregator*> live;
};
Owners& owners() {
    static Owners* owners = new Owners();
    return *owners;
}

// FNV-1a over the frame ids and the type: one call site can allocate several types.
std::uint64_t siteHash(std::span<const FrameId> frames, ClassID classId) {
    std::uint64_t h = 1469598103934665603ull;
    for (FrameId frame : frames) {
        h ^= frame;
        h *= 1099511628211ull;
    }
    return (h ^ static_cast<std::uint64_t>(classId)) * 1099511628211ull;
}

// The site with exactly these frames and type, or null.
Aggregator::Site* findSite(Aggregator::SiteMap& sites, std::uint64_t hash, std::span<const FrameId> frames, ClassID classId) {
    auto [first, last] = sites.equal_range(hash);
    for (auto it = first; it != last; ++it) {
        if (it->second.classId == classId && std::ranges::equal(it->second.frames, frames)) {
            return &it->second;
        }
    }
    return nullptr;
}

// A TypeDef's metadata name as UTF-8, as ClrMD spells it; empty when it can't be read.
std::string typeDefName(IMetaDataImport* md, mdTypeDef typeDef, DWORD& flags) {
    std::vector<WCHAR> buffer(512);
    for (int attempt = 0; attempt < 2; ++attempt) {
        ULONG length = 0;
        if (FAILED(md->GetTypeDefProps(typeDef, buffer.data(), static_cast<ULONG>(buffer.size()), &length, &flags, nullptr)))
            return {};
        if (length <= buffer.size()) {
            std::span<const WCHAR> units(buffer.data(), length);
            units = units.first(static_cast<std::size_t>(std::ranges::find(units, WCHAR{0}) - units.begin()));
            return utf8(units, /*replaceInvalid*/ true).value_or(std::string());
        }
        buffer.resize(length);
    }
    return {};
}

// ClrMD-compatible primitive names; other element types need ClassID resolution.
const char* primitiveElementName(CorElementType t) {
    switch (t) {
        case ELEMENT_TYPE_BOOLEAN: return "System.Boolean";
        case ELEMENT_TYPE_CHAR:    return "System.Char";
        case ELEMENT_TYPE_I1:      return "System.SByte";
        case ELEMENT_TYPE_U1:      return "System.Byte";
        case ELEMENT_TYPE_I2:      return "System.Int16";
        case ELEMENT_TYPE_U2:      return "System.UInt16";
        case ELEMENT_TYPE_I4:      return "System.Int32";
        case ELEMENT_TYPE_U4:      return "System.UInt32";
        case ELEMENT_TYPE_I8:      return "System.Int64";
        case ELEMENT_TYPE_U8:      return "System.UInt64";
        case ELEMENT_TYPE_R4:      return "System.Single";
        case ELEMENT_TYPE_R8:      return "System.Double";
        case ELEMENT_TYPE_STRING:  return "System.String";
        case ELEMENT_TYPE_I:       return "System.IntPtr";
        case ELEMENT_TYPE_U:       return "System.UIntPtr";
        case ELEMENT_TYPE_OBJECT:  return "System.Object";
        default:                   return "";
    }
}

// A TypeDef's name and those of its enclosing types, outermost first. Generic arity markers ("`2") are kept;
// constructedTypeName replaces them with the type arguments.
std::vector<std::string> typeDefSegments(IMetaDataImport* md, mdTypeDef typeDef) {
    std::vector<std::string> segments;
    mdTypeDef cur = typeDef;
    for (;;) {
        DWORD flags = 0;
        std::string name = typeDefName(md, cur, flags);
        if (name.empty())
            break;
        segments.insert(segments.begin(), std::move(name));
        if (!IsTdNested(flags))
            break;
        mdTypeDef enclosing = 0;
        if (FAILED(md->GetNestedClassProps(cur, &enclosing)) || enclosing == cur)
            break;
        cur = enclosing;
    }
    return segments;
}

} // namespace

Aggregator::Aggregator(ICorProfilerInfo10* info, Logger* logger, MethodRegistry& methods)
    : info_(info), logger_(logger), methods_(methods),
      instanceId_(g_nextAggregatorId.fetch_add(1, std::memory_order_relaxed)) {
    std::lock_guard lock(owners().mutex);
    owners().live.emplace(instanceId_, this);
}

Aggregator::~Aggregator() {
    {
        std::lock_guard lock(owners().mutex);
        owners().live.erase(instanceId_);
    }
    for (std::atomic<Shard*>& slot : shards_) {
        delete slot.load(std::memory_order_acquire);
    }
}

thread_local Aggregator::ThreadExit Aggregator::threadExit_;

Aggregator::ThreadExit::~ThreadExit() {
    releaseThreadShard();
    t_exited = true;
}

void Aggregator::releaseThreadShard() noexcept {
    const ThreadShard held = std::exchange(t_shard, {});
    if (held.shard == nullptr) {
        return;
    }
    try {
        std::lock_guard lock(owners().mutex);
        auto it = owners().live.find(held.ownerId);
        if (it == owners().live.end() || held.shard == &it->second->overflow_) {
            return; // a destroyed aggregator freed its shards; the overflow shard stays shared
        }
        Aggregator& owner = *it->second;
        std::lock_guard shardsLock(owner.shardsMutex_);
        owner.freeShards_.push_back(held.shard);
    } catch (...) {
        // Out of memory growing the free list: the shard stays registered and is dumped, just not reused.
    }
}

std::size_t Aggregator::shardCount() const {
    return registeredShards().size();
}

// Reserve initial shard capacity; pending retains its capacity across GCs.
namespace {
// Flushes a written file to disk, so a crash right after publishing it can't leave a partial profile.
bool syncFile(const std::string& path) {
#ifdef _WIN32
    HANDLE file = CreateFileA(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        return false;
    }
    const bool flushed = FlushFileBuffers(file) != 0;
    CloseHandle(file);
    return flushed;
#else
    const int fd = ::open(path.c_str(), O_WRONLY);
    if (fd < 0) {
        return false;
    }
    const bool flushed = ::fsync(fd) == 0;
    ::close(fd);
    return flushed;
#endif
}

// Flushes the rename that published a file. Windows' write-through move already did; some POSIX file systems can't
// sync a directory, which only weakens the guarantee.
void syncParentDirectory([[maybe_unused]] const std::string& path) {
#ifndef _WIN32
    std::string dir = std::filesystem::path(path).parent_path().string();
    const int fd = ::open(dir.empty() ? "." : dir.c_str(), O_RDONLY);
    if (fd >= 0) {
        (void)::fsync(fd);
        ::close(fd);
    }
#endif
}

constexpr std::size_t kSitesReserve = 4096;    // distinct allocation stacks per thread
constexpr std::size_t kPendingReserve = 2048;  // sampled objects awaiting their first GC
} // namespace

Aggregator::Shard& Aggregator::localShard() {
    if (t_shard.ownerId != instanceId_) [[unlikely]] {
        if (t_exited) {
            return overflow_;
        }
        releaseThreadShard(); // one from another aggregator goes back to it
        t_shard = {instanceId_, &takeShard()};
        threadExit_.armed = true;
    }
    return *t_shard.shard;
}

Aggregator::Shard& Aggregator::takeShard() {
    std::lock_guard lock(shardsMutex_);
    if (!freeShards_.empty()) {
        Shard* shard = freeShards_.back();
        freeShards_.pop_back();
        return *shard;
    }
    const int count = shardCount_.load(std::memory_order_relaxed);
    if (count < kMaxShards) {
        auto* shard = new Shard();
        shard->sites.reserve(kSitesReserve);
        shard->pending.reserve(kPendingReserve);
        shards_[count].store(shard, std::memory_order_release);
        shardCount_.store(count + 1, std::memory_order_release);
        return *shard;
    }
    if (!overflowReported_ && logger_) {
        logger_->warn("more than {} threads are recording allocations at once; the others share one shard", kMaxShards);
    }
    overflowReported_ = true;
    return overflow_;
}

void Aggregator::record(std::span<const FrameId> frames, std::uint64_t bytes, ObjectID addr, ClassID classId) {
    const std::uint64_t hash = siteHash(frames, classId);
    Shard& shard = localShard();
    std::lock_guard lock(shard.mutex);

    Site* site = findSite(shard.sites, hash, frames, classId);
    if (site == nullptr) {
        Site fresh;
        fresh.frames.assign(frames.begin(), frames.end());
        fresh.classId = classId;
        site = &shard.sites.emplace(hash, std::move(fresh))->second;
    }

    site->alloc.count += 1;
    site->alloc.bytes += bytes;
    shard.pending.push_back({addr, bytes, site});
}

void Aggregator::beginGc() {
    survivorRanges_.clear();
    condemnedRanges_.clear();
    largeObjectRanges_.clear();
}

void Aggregator::noteCondemnedRange(ObjectID start, std::uint64_t length) {
    std::lock_guard<std::mutex> lock(noteMutex_); // Server GC: concurrent per-heap callbacks
    condemnedRanges_.emplace_back(static_cast<std::uint64_t>(start),
                                  static_cast<std::uint64_t>(start) + length);
}

void Aggregator::noteLargeObjectRange(ObjectID start, std::uint64_t length) {
    std::lock_guard<std::mutex> lock(noteMutex_);
    largeObjectRanges_.emplace_back(static_cast<std::uint64_t>(start),
                                    static_cast<std::uint64_t>(start) + length);
}

void Aggregator::noteSurvivorRange(ObjectID start, std::uint64_t length) {
    std::lock_guard<std::mutex> lock(noteMutex_);
    survivorRanges_.emplace_back(static_cast<std::uint64_t>(start),
                                 static_cast<std::uint64_t>(start) + length);
}

void Aggregator::noteMove(ObjectID oldStart, ObjectID newStart, std::uint64_t length) {
    // Test liveness at the old address before following the relocation.
    std::lock_guard<std::mutex> lock(noteMutex_);
    survivorRanges_.emplace_back(static_cast<std::uint64_t>(oldStart),
                                 static_cast<std::uint64_t>(oldStart) + length);
    if (correlate_)
        moves_.push_back({static_cast<std::uint64_t>(oldStart),
                          static_cast<std::uint64_t>(newStart), length});
}

ObjectID Aggregator::remap(ObjectID addr) const {
    return static_cast<ObjectID>(intervals::remap(static_cast<std::uint64_t>(addr), moves_));
}

bool Aggregator::survived(ObjectID addr) const {
    return intervals::inSortedRanges(static_cast<std::uint64_t>(addr), survivorRanges_);
}

bool Aggregator::condemned(ObjectID addr) const {
    return intervals::inSortedRanges(static_cast<std::uint64_t>(addr), condemnedRanges_);
}

bool Aggregator::inLargeObjectHeap(ObjectID addr) const {
    return intervals::inSortedRanges(static_cast<std::uint64_t>(addr), largeObjectRanges_);
}

void Aggregator::endGc() {
    std::unique_lock correlationLock(correlationMutex_, std::defer_lock);
    if (correlate_) {
        correlationLock.lock();
    }

    // Range queries require address order; skip sorting already-ordered callbacks.
    if (!std::is_sorted(survivorRanges_.begin(), survivorRanges_.end()))
        std::sort(survivorRanges_.begin(), survivorRanges_.end());
    if (!std::is_sorted(condemnedRanges_.begin(), condemnedRanges_.end()))
        std::sort(condemnedRanges_.begin(), condemnedRanges_.end());
    if (!std::is_sorted(largeObjectRanges_.begin(), largeObjectRanges_.end()))
        std::sort(largeObjectRanges_.begin(), largeObjectRanges_.end());
    if (correlate_ && !std::is_sorted(moves_.begin(), moves_.end(),
            [](const intervals::MoveRange& a, const intervals::MoveRange& b) { return a.oldStart < b.oldStart; }))
        std::sort(moves_.begin(), moves_.end(),
                  [](const intervals::MoveRange& a, const intervals::MoveRange& b) { return a.oldStart < b.oldStart; });

    // Uncondemned LOH/POH objects are alive even without an ephemeral-GC survivor report.
    if (correlate_)
        newSurvivors_.clear();
    for (Shard* shard : registeredShards()) {
        std::lock_guard shardLock(shard->mutex);
        for (const Pending& p : shard->pending) {
            bool aliveUnexamined = inLargeObjectHeap(p.addr) && !condemned(p.addr);
            if (survived(p.addr) || aliveUnexamined) {
                p.site->survived.count += 1;
                p.site->survived.bytes += p.bytes;
                if (correlate_)
                    newSurvivors_.push_back({remap(p.addr), nextObjectId_.fetch_add(1), p.site});
            }
        }
        shard->pending.clear();
    }

    if (correlate_) {
        // Sweep only the address window this GC could change; carry the rest verbatim.
        auto byAddr = [](const LiveEntry& a, const LiveEntry& b) { return a.addr < b.addr; };
        std::sort(newSurvivors_.begin(), newSurvivors_.end(), byAddr);

        // Window [windowStart, windowEnd). Empty condemnedRanges_ = whole heap condemned (full GC).
        std::uint64_t windowStart, windowEnd;
        if (condemnedRanges_.empty()) {
            windowStart = 0;
            windowEnd = std::numeric_limits<std::uint64_t>::max();
        } else {
            windowStart = condemnedRanges_.front().first;
            windowEnd = condemnedRanges_.back().second;
            // Include move targets and fresh survivors so the splice preserves global order.
            for (const intervals::MoveRange& m : moves_) {
                windowStart = std::min(windowStart, m.newStart);
                windowEnd = std::max(windowEnd, m.newStart + m.length);
            }
            if (!newSurvivors_.empty()) {
                windowStart = std::min(windowStart, static_cast<std::uint64_t>(newSurvivors_.front().addr));
                windowEnd = std::max(windowEnd, static_cast<std::uint64_t>(newSurvivors_.back().addr) + 1);
            }
        }

        // [lo, hi) are the only live_ entries that can change.
        std::size_t lo = static_cast<std::size_t>(
            std::lower_bound(live_.begin(), live_.end(), windowStart,
                             [](const LiveEntry& e, std::uint64_t v) { return e.addr < v; }) - live_.begin());
        std::size_t hi = static_cast<std::size_t>(
            std::upper_bound(live_.begin(), live_.end(), windowEnd,
                             [](std::uint64_t v, const LiveEntry& e) { return v < e.addr; }) - live_.begin());

        // Carry uncondemned objects, remap survivors and drop deaths. Cross-heap moves can
        // reorder addresses; record each descending transition for the subsequent run merge.
        intervals::ForwardCursor cursor(survivorRanges_, moves_, condemnedRanges_);
        windowScratch_.clear();
        windowScratch_.reserve((hi - lo) + newSurvivors_.size());
        runStarts_.clear();
        std::uint64_t prevAddr = 0;
        bool havePrev = false;
        for (std::size_t i = lo; i < hi; ++i) {
            const LiveEntry& e = live_[i];
            std::uint64_t out;
            if (!cursor.condemned(e.addr)) {
                out = e.addr;
            } else if (cursor.survived(e.addr)) {
                out = cursor.remap(e.addr);
            } else {
                continue; // dead
            }
            if (!havePrev || out < prevAddr) {
                runStarts_.push_back(windowScratch_.size()); // new ascending run
            }
            windowScratch_.push_back({static_cast<ObjectID>(out), e.id, e.site});
            prevAddr = out;
            havePrev = true;
        }

        // Merge remapped runs with fresh survivors unless the result is already sorted.
        std::vector<LiveEntry>* merged;
        std::size_t nRuns = runStarts_.size();
        if (nRuns <= 1 && newSurvivors_.empty()) {
            merged = &windowScratch_;
        } else {
            struct Run { const LiveEntry* cur; const LiveEntry* end; };
            static thread_local std::vector<Run> runs; // GC thread only; capacity retained
            runs.clear();
            for (std::size_t r = 0; r < nRuns; ++r) {
                std::size_t s = runStarts_[r];
                std::size_t e = (r + 1 < nRuns) ? runStarts_[r + 1] : windowScratch_.size();
                if (e > s) runs.push_back({windowScratch_.data() + s, windowScratch_.data() + e});
            }
            if (!newSurvivors_.empty())
                runs.push_back({newSurvivors_.data(), newSurvivors_.data() + newSurvivors_.size()});

            mergeOut_.clear();
            mergeOut_.reserve(windowScratch_.size() + newSurvivors_.size());
            // Lower-indexed runs win ties: carried survivors precede fresh ones,
            // so deduplication below preserves the carried identity.
            for (;;) {
                int best = -1;
                std::uint64_t bestAddr = 0;
                for (int r = 0; r < static_cast<int>(runs.size()); ++r) {
                    if (runs[r].cur == runs[r].end) continue;
                    if (best < 0 || runs[r].cur->addr < bestAddr) {
                        best = r;
                        bestAddr = runs[r].cur->addr;
                    }
                }
                if (best < 0) break;
                mergeOut_.push_back(*runs[best].cur++);
            }
            merged = &mergeOut_;
        }

        // Keep the carried identity when a fresh survivor has the same address.
        merged->erase(
            std::unique(merged->begin(), merged->end(),
                        [](const LiveEntry& a, const LiveEntry& b) { return a.addr == b.addr; }),
            merged->end());

        // Splice at lo, shifting only the suffix; the untouched prefix stays in place.
        live_.erase(live_.begin() + lo, live_.begin() + hi);
        live_.insert(live_.begin() + lo, merged->begin(), merged->end());

#ifndef NDEBUG
        assert(std::is_sorted(live_.begin(), live_.end(), byAddr) &&
               "windowed endGc must leave live_ globally sorted");
#endif
    }

    survivorRanges_.clear();
    moves_.clear();
}

void Aggregator::countPendingAsSurvived() {
    std::unique_lock correlationLock(correlationMutex_, std::defer_lock);
    if (correlate_) {
        correlationLock.lock();
    }

    // Uncollected pending objects are still alive at shutdown.
    std::size_t appended = 0;
    for (Shard* shard : registeredShards()) {
        std::lock_guard shardLock(shard->mutex);
        for (const Pending& p : shard->pending) {
            p.site->survived.count += 1;
            p.site->survived.bytes += p.bytes;
            if (correlate_) {
                live_.push_back({p.addr, nextObjectId_.fetch_add(1), p.site});
                ++appended;
            }
        }
        shard->pending.clear();
    }
    if (correlate_ && appended > 0) {
        std::sort(live_.begin(), live_.end(),
                  [](const LiveEntry& a, const LiveEntry& b) { return a.addr < b.addr; });
        // Keep one identity per address.
        live_.erase(std::unique(live_.begin(), live_.end(),
                                [](const LiveEntry& a, const LiveEntry& b) { return a.addr == b.addr; }),
                    live_.end());
    }
}

std::vector<Aggregator::Shard*> Aggregator::registeredShards() const {
    std::vector<Shard*> result;
    const int count = shardCount_.load(std::memory_order_acquire);
    result.reserve(static_cast<std::size_t>(count) + 1);
    for (int i = 0; i < count; ++i) {
        result.push_back(shards_[i].load(std::memory_order_acquire));
    }
    result.push_back(&overflow_);
    return result;
}

Aggregator::SiteMap Aggregator::mergeShards(std::span<Shard* const> shards) {
    SiteMap merged;
    for (Shard* shard : shards) {
        for (auto& [hash, site] : shard->sites) {
            if (Site* existing = findSite(merged, hash, site.frames, site.classId)) {
                existing->alloc.count += site.alloc.count;
                existing->alloc.bytes += site.alloc.bytes;
                existing->survived.count += site.survived.count;
                existing->survived.bytes += site.survived.bytes;
            } else {
                merged.emplace(hash, site);
            }
        }
    }
    return merged;
}

void Aggregator::captureState(SiteMap& sites, std::vector<LiveEntry>* live) {
    std::lock_guard snapshotLock(snapshotMutex_);
    std::vector<Shard*> shards = registeredShards();

    // Correlation updates acquire this lock before shard locks; preserve that order.
    std::unique_lock correlationLock(correlationMutex_, std::defer_lock);
    if (live != nullptr) {
        correlationLock.lock();
    }

    std::vector<std::unique_lock<std::mutex>> shardLocks;
    shardLocks.reserve(shards.size());
    for (Shard* shard : shards) {
        shardLocks.emplace_back(shard->mutex);
    }

    sites = mergeShards(shards);
    if (live != nullptr) {
        *live = live_;
    }
}

// The methods of one profile write. Each frame is described once, and the writer keeps methods apart by
// identity (module MVID and token), so two different methods never share a record whatever their names.
class Aggregator::ProfileMethods {
public:
    ProfileMethods(const MethodRegistry& registry, storage::ProvenanceWriter& writer) : registry_(registry), writer_(writer) {}

    std::uint32_t stack(std::span<const FrameId> frames) {
        scratch_.clear();
        for (FrameId frame : frames) {
            scratch_.push_back(method(frame));
        }
        return writer_.internStack(scratch_);
    }

private:
    std::uint32_t method(FrameId frame) {
        auto [it, inserted] = indices_.try_emplace(frame, 0u);
        if (inserted) {
            const MethodRegistry::Method m = registry_.describe(frame);
            it->second = writer_.internMethod({m.name.mvid, m.token, m.name.module, m.name.ns, m.name.type, m.name.name,
                                               m.name.genericParameters, m.name.parameters, m.name.qualifiedParameters,
                                               m.name.returnType, m.name.qualifiedReturnType, m.problem});
        }
        return it->second;
    }

    const MethodRegistry& registry_;
    storage::ProvenanceWriter& writer_;
    std::unordered_map<FrameId, std::uint32_t> indices_;
    std::vector<std::uint32_t> scratch_;
};

void Aggregator::writeProfile(storage::ProvenanceWriter& pw, ProfileMethods& methods, const SiteMap& sites) {
    for (const auto& [hash, site] : sites) {
        const std::uint32_t stackId = methods.stack(site.frames);
        const std::uint32_t typeId = pw.internType(resolveTypeName(site.classId));
        pw.addAllocation(stackId, typeId, site.alloc.bytes, site.alloc.count, site.survived.bytes, site.survived.count);
    }
}

// A snapshot's unified provenance.slab: the allocation profile plus per-object correlation over one
// shared stack table. sl joins the correlation to a heap dump by address.
bool Aggregator::emitCorrelation(const std::string& path) noexcept {
    try {
        SiteMap merged;
        std::vector<LiveEntry> live;
        captureState(merged, &live);

        storage::ProvenanceWriter pw;
        ProfileMethods methods(methods_, pw);
        writeProfile(pw, methods, merged);

        // Site frames and class IDs are immutable, so resolve them outside shard locks.
        std::unordered_map<const Site*, std::uint32_t> siteStack;
        for (const LiveEntry& lv : live) {
            auto [it, inserted] = siteStack.try_emplace(lv.site, 0u);
            if (inserted) {
                it->second = methods.stack(lv.site->frames);
            }
            pw.addObject(static_cast<std::uint64_t>(lv.addr), it->second);
        }

        if (!writeSlab(path, pw)) {
            return false;
        }
        if (logger_) {
            logger_->trace("wrote provenance ({} stacks, {} live objects) to {}", merged.size(), live.size(), path);
        }
        return true;
    } catch (const std::exception& ex) {
        if (logger_) {
            logger_->error("could not build provenance snapshot: {}", ex.what());
        }
    } catch (...) {
        if (logger_) {
            logger_->error("could not build provenance snapshot");
        }
    }
    return false;
}

// Exit-time (or live-flush) allocation aggregate: allocations only, no correlation.
bool Aggregator::dump(const std::string& path) noexcept {
    try {
        SiteMap merged;
        captureState(merged, nullptr);
        storage::ProvenanceWriter pw;
        ProfileMethods methods(methods_, pw);
        writeProfile(pw, methods, merged);

        if (!writeSlab(path, pw)) {
            return false;
        }
        if (logger_) {
            logger_->trace("wrote {} stacks to {}", merged.size(), path);
        }
        return true;
    } catch (const std::exception& ex) {
        if (logger_) {
            logger_->error("could not build allocation snapshot: {}", ex.what());
        }
    } catch (...) {
        if (logger_) {
            logger_->error("could not build allocation snapshot");
        }
    }
    return false;
}

bool Aggregator::writeSlab(const std::string& path, storage::ProvenanceWriter& pw) {
    storage::ContainerWriter cw;
    pw.writeTo(cw);
    const std::string temp =
        path + ".tmp-" + std::to_string(instanceId_) + "-" +
        std::to_string(writeSequence_.fetch_add(1, std::memory_order_relaxed));

#ifndef _WIN32
    const int fd = ::open(
        temp.c_str(), O_WRONLY | O_CREAT | O_EXCL, S_IRUSR | S_IWUSR);
    if (fd < 0) {
        if (logger_) {
            logger_->error("could not create private profile output: {}", path);
        }
        return false;
    }
    ::close(fd);
#endif

    std::ofstream out(temp, std::ios::binary | std::ios::trunc);
    if (!out.is_open()) {
        std::remove(temp.c_str());
        if (logger_)
            logger_->error("could not open profile output: {}", path);
        return false;
    }

    bool written = cw.writeTo(out);
    out.flush();
    written = written && out.good();
    out.close();
    written = written && !out.fail() && syncFile(temp);
    if (!written) {
        std::remove(temp.c_str());
        if (logger_) {
            logger_->error("could not write profile output: {}", path);
        }
        return false;
    }

#ifdef _WIN32
    const bool published = MoveFileExA(
        temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != 0;
#else
    const bool published = std::rename(temp.c_str(), path.c_str()) == 0;
#endif
    if (!published) {
        std::remove(temp.c_str());
        if (logger_) {
            logger_->error("could not publish profile output: {}", path);
        }
        return false;
    }
    syncParentDirectory(path);
    return true;
}

const std::string& Aggregator::resolveTypeName(ClassID classId) {
    {
        std::lock_guard lock(typeNameMutex_);
        auto cached = typeNameCache_.find(classId);
        if (cached != typeNameCache_.end())
            return cached->second;
    }

    // CLR metadata calls and recursive array resolution must run outside the cache lock.
    std::string name = resolveTypeNameUncached(classId);
    std::lock_guard lock(typeNameMutex_);
    return typeNameCache_.emplace(classId, std::move(name)).first->second;
}

std::vector<Aggregator::LiveObjectInfo> Aggregator::inspectLiveObjects() const {
    std::lock_guard lock(correlationMutex_);
    std::vector<LiveObjectInfo> out;
    out.reserve(live_.size());
    for (const LiveEntry& entry : live_) {
        out.push_back({static_cast<std::uint64_t>(entry.addr), entry.id});
    }
    return out;
}

std::string Aggregator::resolveTypeNameUncached(ClassID classId) {
    if (classId == 0 || info_ == nullptr)
        return "<unknown>";

    // Arrays have no TypeDef. Resolve jagged elements recursively and use ClrMD rank syntax.
    CorElementType elementType{};
    ClassID elementClass = 0;
    ULONG rank = 0;
    if (info_->IsArrayClass(classId, &elementType, &elementClass, &rank) == S_OK) {
        std::string element;
        if (elementType == ELEMENT_TYPE_FNPTR) {
            element = "System.IntPtr"; // ClrMD's name for function-pointer elements
        } else if (elementType == ELEMENT_TYPE_PTR) {
            element = "<pointer>"; // the profiling API doesn't expose the pointee, which ClrMD shows (System.Int32*)
        } else {
            // The element's own type first: an enum array reports its underlying primitive as the element type.
            if (elementClass != 0)
                element = resolveTypeName(elementClass);
            if (element.empty() || element == "<unknown>") {
                const char* primitive = primitiveElementName(elementType);
                element = primitive[0] != '\0' ? primitive : elementClass != 0 ? element : "System.Object";
            }
        }

        std::string brackets = "[";
        for (ULONG i = 1; i < rank; ++i)
            brackets += ',';
        brackets += ']';
        return element + brackets;
    }

    ModuleID moduleId = 0;
    mdTypeDef typeDef = 0;
    ClassID parent = 0;
    ULONG32 argumentCount = 0;
    std::vector<ClassID> argumentIds;
    // The first call reports the module, TypeDef and argument count; the CLR truncates to the buffer otherwise.
    HRESULT hr = info_->GetClassIDInfo2(classId, &moduleId, &typeDef, &parent, 0, &argumentCount, nullptr);
    if (SUCCEEDED(hr) && argumentCount > 0) {
        argumentIds.resize(argumentCount);
        hr = info_->GetClassIDInfo2(classId, &moduleId, &typeDef, &parent, argumentCount, &argumentCount, argumentIds.data());
    }
    if (FAILED(hr)) {
        // Without the arguments the open name ("List`1") is still better than nothing.
        argumentCount = 0;
        if (FAILED(info_->GetClassIDInfo(classId, &moduleId, &typeDef)))
            return "<unknown>";
    }
    if (typeDef == 0)
        return "<unknown>";

    IMetaDataImport* md = nullptr;
    if (FAILED(info_->GetModuleMetaData(moduleId, ofRead, IID_IMetaDataImport, (IUnknown**)&md)) || md == nullptr)
        return "<unknown>";
    std::vector<std::string> segments = typeDefSegments(md, typeDef);
    md->Release();
    if (segments.empty())
        return "<unknown>";

    std::vector<std::string> arguments;
    arguments.reserve(argumentCount);
    for (ULONG32 i = 0; i < argumentCount; ++i)
        arguments.push_back(resolveTypeName(argumentIds[i]));
    if (std::optional<std::string> constructed = signature::constructedTypeName(segments, arguments))
        return *constructed;

    std::string open;
    for (const std::string& segment : segments)
        open += (open.empty() ? "" : "+") + segment;
    return open;
}

} // namespace Sherlock
