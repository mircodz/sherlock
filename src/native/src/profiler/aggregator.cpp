#include "sherlock/profiler/aggregator.hpp"

#include "sherlock/common/logger.hpp"
#include "sherlock/storage/profile.hpp"

#include <algorithm>
#include <cassert>
#include <cerrno>
#include <cstdio>
#include <cstdint>
#include <fstream>
#include <limits>
#include <memory>
#include <system_error>
#include <string_view>
#include <unordered_map>
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

// One shard pointer per thread, tagged with its owning Aggregator's id. The tag lets a new
// Aggregator on the same thread allocate a fresh shard instead of reusing the previous one's
// (freed in its destructor: use-after-free). Keyed on a monotonic id, not `this`, because a
// stack-allocated Aggregator can be reborn at a just-destroyed one's address.
struct ThreadShard {
    std::uint64_t ownerId = 0;
    Aggregator::Shard* shard = nullptr;
};
thread_local ThreadShard t_shard;

std::atomic<std::uint64_t> g_nextAggregatorId{1};

// FNV-1a over the frame ids.
std::uint64_t hashFrames(std::span<const FunctionID> frames) {
    std::uint64_t h = 1469598103934665603ull;
    for (FunctionID f : frames) {
        h ^= static_cast<std::uint64_t>(f);
        h *= 1099511628211ull;
    }
    return h;
}

/// Narrows a UTF-16 metadata string to ASCII (type/method names are ASCII); non-ASCII
/// code units become '?'. Portable across the WCHAR/wchar_t difference between Windows
/// and the Unix PAL.
std::string narrow(const WCHAR* s, ULONG len) {
    std::string out;
    out.reserve(len);
    for (ULONG i = 0; i < len && s[i] != 0; ++i)
        out.push_back(s[i] < 128 ? static_cast<char>(s[i]) : '?');
    return out;
}

// The BCL name for a primitive array element, so a Double[] reads "System.Double[]" as ClrMD spells
// it. Empty for anything not a primitive element type (the caller resolves those via the class id).
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

// A typeDef's name, joining enclosing types with '+' as ClrMD does (e.g. "System.Collections.
// Generic.List`1+Enumerator"). GetTypeDefProps gives a nested type only its leaf name, so we climb
// GetNestedClassProps to the outermost, which carries the namespace. Generic arity stays as the
// metadata backtick suffix, matching ClrMD.
std::string typeDefName(IMetaDataImport* md, mdTypeDef typeDef) {
    std::string name;
    mdTypeDef cur = typeDef;
    for (;;) {
        WCHAR buf[512];
        ULONG len = 0;
        DWORD flags = 0;
        if (FAILED(md->GetTypeDefProps(cur, buf, 512, &len, &flags, nullptr)))
            break;
        std::string part = narrow(buf, len);
        name = name.empty() ? part : part + "+" + name;
        if (!IsTdNested(flags))
            break;
        mdTypeDef enclosing = 0;
        if (FAILED(md->GetNestedClassProps(cur, &enclosing)) || enclosing == cur)
            break;
        cur = enclosing;
    }
    return name;
}

} // namespace

Aggregator::Aggregator(ICorProfilerInfo10* info, Logger* logger)
    : info_(info), logger_(logger),
      instanceId_(g_nextAggregatorId.fetch_add(1, std::memory_order_relaxed)) {
}

Aggregator::~Aggregator() {
    for (std::atomic<Shard*>& slot : shards_) {
        delete slot.load(std::memory_order_acquire);
    }
}

// Reserve the per-thread shard structures up front so the hot path never pays for a map rehash
// or a pending realloc mid-allocation. `pending` is clear()ed (not freed) each GC, keeping capacity.
namespace {
constexpr std::size_t kSitesReserve = 4096;    // distinct allocation stacks per thread
constexpr std::size_t kPendingReserve = 2048;  // sampled objects awaiting their first GC
} // namespace

Aggregator::Shard& Aggregator::localShard() {
    if (t_shard.ownerId != instanceId_) {
        auto* shard = new Shard();
        shard->sites.reserve(kSitesReserve);
        shard->pending.reserve(kPendingReserve);
        int idx = shardCount_.fetch_add(1, std::memory_order_acq_rel);
        if (idx < kMaxShards)
            shards_[idx].store(shard, std::memory_order_release);
        // else: too many threads, shard still works locally but isn't dumped.
        t_shard = {instanceId_, shard};
    }
    return *t_shard.shard;
}

void Aggregator::record(std::span<const FunctionID> frames, std::uint64_t bytes, ObjectID addr, ClassID classId) {
    // Key by (stack, type): mix classId into the stack hash so one call site allocating two types
    // lands in two sites. A key collision across distinct pairs would only merge counts.
    std::uint64_t key = hashFrames(frames);
    key = (key ^ static_cast<std::uint64_t>(classId)) * 1099511628211ull;
    Shard& shard = localShard();
    std::lock_guard lock(shard.mutex);

    auto it = shard.sites.find(key);
    Site* site;
    if (it == shard.sites.end()) {
        Site fresh;
        fresh.frames.assign(frames.begin(), frames.end());
        fresh.classId = classId;
        site = &shard.sites.emplace(key, std::move(fresh)).first->second;
    } else {
        site = &it->second;
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
    // The old range is also a survivor span (for the liveness test); the old->new delta lets us
    // follow the object's identity to its new address.
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

    // Sort the range vectors (required by inSortedRanges / ForwardCursor). The runtime tends to
    // report these already in address order, so guard each sort behind an is_sorted check.
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

    // Fold per-site survived-stats and collect this GC's fresh survivors. An object survives if it's
    // in a survivor span, OR it's on the LOH/POH but uncondemned: an ephemeral GC never reports large-
    // object survivors, yet they're alive (not examined). Without this, large objects would be dropped
    // from `pending` at the first ephemeral GC.
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
        // live_ is sorted by address and grows monotonically, so touching all of it every GC makes
        // frequent gen-0 GCs progressively slower. Sweep only the contiguous address window this GC
        // could have changed; everything outside is carried verbatim. A full GC condemns the whole
        // heap, so window = whole vector.
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
            // A relocated entry (move target) or fresh survivor must fall inside the window, else the
            // splice would misorder it; fold both into the bounds.
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

        // (a) Sweep live_[lo, hi) into windowScratch_: carry uncondemned survivors verbatim, remap
        // collected survivors, drop the dead. Compaction preserves order within a heap, so the output
        // is K ascending runs (K = interleaving GC heaps; 1 for Workstation GC). A run boundary is
        // where an emitted address drops, recorded free, so we k-way merge instead of re-sorting.
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

        // (b) Merge the K swept runs (plus newSurvivors_) into sorted order. One run and no fresh
        // survivors: already sorted, skip the merge (the Workstation-GC fast path).
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
            // K is tiny (heaps ~ cores, plus one). A flat min-scan over the run heads beats a heap's
            // cache misses at this K. Ties resolve to the lower-indexed run so a carried survivor wins
            // over a colliding fresh one (swept runs precede newSurvivors_); dedup below drops the loser.
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

        // Drop duplicate addresses (a fresh survivor colliding with a carried one; keep the carried
        // identity if it happens).
        merged->erase(
            std::unique(merged->begin(), merged->end(),
                        [](const LiveEntry& a, const LiveEntry& b) { return a.addr == b.addr; }),
            merged->end());

        // (c) Splice the merged window back at lo. erase/insert shift only the suffix live_[hi,end);
        // the large, growing prefix live_[0,lo) is untouched, so per-GC cost is O(window + suffix).
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

    // At shutdown, anything still pending was never collected, i.e. still alive. Append the newly
    // discovered live objects and re-sort once (cold, one-shot path).
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
        // A pending object's address may already be tracked (allocated then survived a prior GC);
        // keep the first of any duplicate so each address maps to one identity.
        live_.erase(std::unique(live_.begin(), live_.end(),
                                [](const LiveEntry& a, const LiveEntry& b) { return a.addr == b.addr; }),
                    live_.end());
    }
}

std::vector<Aggregator::Shard*> Aggregator::registeredShards() const {
    std::vector<Shard*> result;
    const int count = std::min(shardCount_.load(std::memory_order_acquire), kMaxShards);
    result.reserve(static_cast<std::size_t>(count));
    for (int i = 0; i < count; ++i) {
        if (Shard* shard = shards_[i].load(std::memory_order_acquire)) {
            result.push_back(shard);
        }
    }
    return result;
}

std::unordered_map<std::uint64_t, Aggregator::Site> Aggregator::mergeShards(
    std::span<Shard* const> shards) {
    std::unordered_map<std::uint64_t, Site> merged;
    for (Shard* shard : shards) {
        for (auto& [key, site] : shard->sites) {
            auto it = merged.find(key);
            if (it == merged.end()) {
                merged.emplace(key, site);
            } else {
                it->second.alloc.count += site.alloc.count;
                it->second.alloc.bytes += site.alloc.bytes;
                it->second.survived.count += site.survived.count;
                it->second.survived.bytes += site.survived.bytes;
            }
        }
    }
    return merged;
}

void Aggregator::captureState(
    std::unordered_map<std::uint64_t, Site>& sites,
    std::vector<LiveEntry>* live) {
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

std::uint32_t Aggregator::internSiteStack(storage::ProvenanceWriter& pw, const Site& site) {
    std::vector<std::string_view> names;
    names.reserve(site.frames.size());
    for (const FunctionID f : site.frames) // stored root->leaf; intern in the same order
        names.push_back(resolveMethodName(f));
    return pw.internStack(names);
}

void Aggregator::writeProfile(storage::ProvenanceWriter& pw, const std::unordered_map<std::uint64_t, Site>& sites) {
    for (const auto& [key, site] : sites) {
        const std::uint32_t stackId = internSiteStack(pw, site);
        const std::uint32_t typeId = pw.internType(resolveTypeName(site.classId));
        pw.addAllocation(stackId, typeId, site.alloc.bytes, site.alloc.count, site.survived.bytes, site.survived.count);
    }
}

// A snapshot's unified provenance.slab: the allocation profile plus per-object correlation over one
// shared stack table. sl joins the correlation to a heap dump by address.
bool Aggregator::emitCorrelation(const std::string& path) noexcept {
    try {
        std::unordered_map<std::uint64_t, Site> merged;
        std::vector<LiveEntry> live;
        captureState(merged, &live);

        storage::ProvenanceWriter pw;
        writeProfile(pw, merged);

        // Site frames and class ids are immutable after insertion, so copied live entries can safely
        // resolve them after the short shard-locking snapshot phase.
        std::unordered_map<const Site*, std::uint32_t> siteStack;
        for (const LiveEntry& lv : live) {
            auto [it, inserted] = siteStack.try_emplace(lv.site, 0u);
            if (inserted) {
                it->second = internSiteStack(pw, *lv.site);
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
        std::unordered_map<std::uint64_t, Site> merged;
        captureState(merged, nullptr);
        storage::ProvenanceWriter pw;
        writeProfile(pw, merged);

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
    written = written && !out.fail();
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
    return true;
}

const std::string& Aggregator::resolveMethodName(FunctionID method) {
    {
        std::lock_guard lock(nameCacheMutex_);
        auto cached = nameCache_.find(method);
        if (cached != nameCache_.end())
            return cached->second;
    }

    std::string name = "<unknown>";
    if (method != 0 && info_ != nullptr) {
        ClassID classId = 0;
        ModuleID moduleId = 0;
        mdToken token = 0;
        if (SUCCEEDED(info_->GetFunctionInfo(method, &classId, &moduleId, &token))) {
            IMetaDataImport* md = nullptr;
            if (SUCCEEDED(info_->GetModuleMetaData(moduleId, ofRead, IID_IMetaDataImport, (IUnknown**)&md)) && md != nullptr) {
                WCHAR methodName[512];
                ULONG methodLen = 0;
                mdTypeDef typeToken = 0;
                if (SUCCEEDED(md->GetMethodProps(token, &typeToken, methodName, 512, &methodLen,
                                                 nullptr, nullptr, nullptr, nullptr, nullptr))) {
                    std::string typeName = "<type>";
                    WCHAR typeName16[512];
                    ULONG typeLen = 0;
                    DWORD typeFlags = 0;
                    if (SUCCEEDED(md->GetTypeDefProps(typeToken, typeName16, 512, &typeLen, &typeFlags, nullptr)))
                        typeName = narrow(typeName16, typeLen);
                    name = typeName + "." + narrow(methodName, methodLen);
                }
                md->Release();
            }
        }
    }

    std::lock_guard lock(nameCacheMutex_);
    return nameCache_.emplace(method, std::move(name)).first->second;
}

const std::string& Aggregator::resolveTypeName(ClassID classId) {
    {
        std::lock_guard lock(nameCacheMutex_);
        auto cached = typeNameCache_.find(classId);
        if (cached != typeNameCache_.end())
            return cached->second;
    }

    std::string name = resolveTypeNameUncached(classId);
    std::lock_guard lock(nameCacheMutex_);
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

    // Arrays have no typeDef; the runtime describes them via IsArrayClass. Format the element name
    // plus one bracket group per dimension ("System.Double[]", "System.Int32[,]"), as ClrMD spells
    // them. The element is itself resolved (recursively for jagged arrays).
    CorElementType elementType{};
    ClassID elementClass = 0;
    ULONG rank = 0;
    if (info_->IsArrayClass(classId, &elementType, &elementClass, &rank) == S_OK) {
        std::string element;
        if (const char* prim = primitiveElementName(elementType); prim[0] != '\0')
            element = prim;
        else if (elementClass != 0)
            element = resolveTypeName(elementClass);
        else
            element = "System.Object";

        std::string brackets = "[";
        for (ULONG i = 1; i < rank; ++i)
            brackets += ',';
        brackets += ']';
        return element + brackets;
    }

    ModuleID moduleId = 0;
    mdTypeDef typeDef = 0;
    if (FAILED(info_->GetClassIDInfo(classId, &moduleId, &typeDef)) || typeDef == 0)
        return "<unknown>";

    IMetaDataImport* md = nullptr;
    if (FAILED(info_->GetModuleMetaData(moduleId, ofRead, IID_IMetaDataImport, (IUnknown**)&md)) || md == nullptr)
        return "<unknown>";

    std::string name = typeDefName(md, typeDef);
    md->Release();
    return name.empty() ? "<unknown>" : name;
}

} // namespace Sherlock
