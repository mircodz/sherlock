#include "sherlock/profiler/aggregator.hpp"

#include "sherlock/common/logger.hpp"
#include "sherlock/storage/profile.hpp"

#include <algorithm>
#include <cassert>
#include <cstdint>
#include <fstream>
#include <limits>
#include <string_view>
#include <unordered_map>
#include <span>
#include <vector>

namespace Sherlock {

namespace {

// One shard pointer per thread. There is a single Aggregator per process, so a
// file-scope thread_local is sufficient (and keeps the hot path branch-free
// after the first allocation on a thread).
thread_local Aggregator::Shard* t_shard = nullptr;

// FNV-1a over the frame ids - cheap and good enough to key distinct stacks.
std::uint64_t hashFrames(std::span<const FunctionID> frames) {
    std::uint64_t h = 1469598103934665603ull;
    for (FunctionID f : frames) {
        h ^= static_cast<std::uint64_t>(f);
        h *= 1099511628211ull;
    }
    return h;
}

/// Narrows a UTF-16 metadata string to ASCII (type/method names are effectively
/// ASCII); non-ASCII code units become '?'. Portable across the WCHAR/wchar_t
/// difference between Windows and the Unix PAL.
std::string narrow(const WCHAR* s, ULONG len) {
    std::string out;
    out.reserve(len);
    for (ULONG i = 0; i < len && s[i] != 0; ++i)
        out.push_back(s[i] < 128 ? static_cast<char>(s[i]) : '?');
    return out;
}

} // namespace

Aggregator::Aggregator(ICorProfilerInfo10* info, Logger* logger)
    : info_(info), logger_(logger) {
}

Aggregator::~Aggregator() {
    int n = shardCount_.load();
    for (int i = 0; i < n && i < kMaxShards; ++i)
        delete shards_[i];
}

// Reserve the per-thread shard structures up front so the hot path never pays for a
// map rehash or a pending-vector realloc mid-allocation - bounded latency on the
// allocating thread. `pending` is clear()ed (not freed) each GC, so it keeps capacity.
namespace {
constexpr std::size_t kSitesReserve = 4096;    // distinct allocation stacks per thread
constexpr std::size_t kPendingReserve = 2048;  // sampled objects awaiting their first GC
} // namespace

Aggregator::Shard& Aggregator::localShard() {
    if (t_shard == nullptr) {
        auto* shard = new Shard();
        shard->sites.reserve(kSitesReserve);
        shard->pending.reserve(kPendingReserve);
        int idx = shardCount_.fetch_add(1, std::memory_order_acq_rel);
        if (idx < kMaxShards)
            shards_[idx] = shard;       // registered; GC sweep & dump will see it
        // else: too many threads - shard still works locally, just isn't dumped.
        t_shard = shard;
    }
    return *t_shard;
}

void Aggregator::record(std::span<const FunctionID> frames, std::uint64_t bytes, ObjectID addr, ClassID classId) {
    // Key by (stack, type): mix the classId into the stack hash so the same call site allocating
    // two types lands in two sites. A key collision across distinct pairs would only merge counts.
    std::uint64_t key = hashFrames(frames);
    key = (key ^ static_cast<std::uint64_t>(classId)) * 1099511628211ull;
    Shard& shard = localShard();

    auto it = shard.sites.find(key);
    Site* site;
    if (it == shard.sites.end()) {
        Site fresh;
        fresh.frames.assign(frames.begin(), frames.end()); // copy the view into the stored site
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
    // The old range is also a survivor span (for the liveness test); the old->new
    // delta additionally lets us follow the object's identity to its new address.
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
    // Sort the range vectors (required by inSortedRanges / ForwardCursor). The runtime tends to report
    // these already in address order, and on a full GC R scales with the surviving population, so guard
    // each sort behind an is_sorted check — O(R) when already ordered, O(R log R) only when not.
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

    // Gather the sampled objects that were allocated since the last GC and survived this one. This
    // also folds the per-site survived-stats (needed whether or not correlation is on). An object
    // "survives" if it appears in this GC's survivor spans, OR if it lives on the LOH/POH but this
    // GC did not condemn it: an ephemeral GC never reports large-object survivors (the LOH isn't
    // collected then), yet the object is plainly still alive — the collection simply didn't examine
    // it. Without this, every large object would be dropped from `pending` at the first ephemeral GC
    // and never tracked. condemned()/inLargeObjectHeap() are binary searches (pending is unsorted).
    if (correlate_)
        newSurvivors_.clear();
    int n = shardCount_.load(std::memory_order_acquire);
    for (int i = 0; i < n && i < kMaxShards; ++i) {
        Shard* shard = shards_[i];
        if (shard == nullptr)
            continue;
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
        // Rebuild the live set. `live_` is sorted by address and grows monotonically for a leaky /
        // accumulating process, so touching all of it every GC makes each (frequent) gen-0 GC
        // progressively slower. Instead we sweep only the contiguous ADDRESS WINDOW that this GC could
        // have changed: an object outside every condemned range is carried verbatim, so only entries
        // inside the condemned span — plus where this GC's survivors/moves relocate, plus where fresh
        // survivors land — can move. Everything below the window (prefix) and above it (suffix) stays
        // in place. Work becomes O(touched), not O(total live). A full gen-2 GC condemns the whole
        // heap, so the window spans the whole vector and this degenerates to the classic full rebuild.
        auto byAddr = [](const LiveEntry& a, const LiveEntry& b) { return a.addr < b.addr; };
        std::sort(newSurvivors_.begin(), newSurvivors_.end(), byAddr);

        // Compute the window [windowStart, windowEnd). Empty condemnedRanges_ means "whole heap
        // condemned" (a full GC that reported no bounds) → sweep everything, matching prior behavior.
        std::uint64_t windowStart, windowEnd;
        if (condemnedRanges_.empty()) {
            windowStart = 0;
            windowEnd = std::numeric_limits<std::uint64_t>::max();
        } else {
            windowStart = condemnedRanges_.front().first;
            windowEnd = condemnedRanges_.back().second;
            // Fold in where survivors are relocated: a move target may land outside the condemned
            // span, and a relocated entry must fall inside the window or the splice would misorder it.
            for (const intervals::MoveRange& m : moves_) {
                windowStart = std::min(windowStart, m.newStart);
                windowEnd = std::max(windowEnd, m.newStart + m.length);
            }
            // Fold in fresh survivors (their remapped addresses), so the merge stays inside the window.
            if (!newSurvivors_.empty()) {
                windowStart = std::min(windowStart, newSurvivors_.front().addr);
                windowEnd = std::max(windowEnd, newSurvivors_.back().addr + 1);
            }
        }

        // Locate the window in the sorted live_: [lo, hi) are the only entries that can change.
        std::size_t lo = static_cast<std::size_t>(
            std::lower_bound(live_.begin(), live_.end(), windowStart,
                             [](const LiveEntry& e, std::uint64_t v) { return e.addr < v; }) - live_.begin());
        std::size_t hi = static_cast<std::size_t>(
            std::upper_bound(live_.begin(), live_.end(), windowEnd,
                             [](std::uint64_t v, const LiveEntry& e) { return v < e.addr; }) - live_.begin());

        // (a) Sweep only the window live_[lo, hi): keep survivors (carried verbatim if uncondemned,
        // remapped if they survived a condemning collection), drop the dead — into windowScratch_.
        // Compaction is order-preserving WITHIN a GC heap, so the swept survivors form K ascending
        // runs (K = number of GC heaps whose remaps interleave; K==1 for Workstation GC). We detect a
        // run boundary for free whenever an emitted address drops below the previous one — same cost as
        // the old is_sorted scan, but it records the run structure so we can k-way MERGE the runs
        // (O(N·K) / O(N log K)) instead of re-SORTING them from scratch (O(N log N)).
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
                out = e.addr;                       // not collected → alive, untouched
            } else if (cursor.survived(e.addr)) {
                out = cursor.remap(e.addr);         // survived (maybe moved)
            } else {
                continue;                           // condemned and not a survivor → dead, drop
            }
            if (!havePrev || out < prevAddr) {
                runStarts_.push_back(windowScratch_.size()); // new ascending run begins here
            }
            windowScratch_.push_back({static_cast<ObjectID>(out), e.id, e.site});
            prevAddr = out;
            havePrev = true;
        }

        // (b) Produce the merged, sorted window into mergeOut_ from: the K swept runs plus, when it has
        // fresh survivors this GC, newSurvivors_ as one more sorted run. Fast paths:
        //   - a single run and no new survivors → windowScratch_ is already sorted, use it directly;
        //   - a single run + new survivors → one 2-way merge (the common Server-GC-free case).
        std::vector<LiveEntry>* merged;
        std::size_t nRuns = runStarts_.size();
        if (nRuns <= 1 && newSurvivors_.empty()) {
            merged = &windowScratch_;               // already sorted, nothing to merge
        } else {
            // Build the run cursor list: each swept run [start, nextStart), then newSurvivors_.
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
            // K is tiny (heaps ≈ cores, plus one). A flat min-scan over the run heads beats a heap's
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

        // Drop duplicate addresses (a fresh survivor colliding with a carried one — shouldn't happen
        // for distinct live objects, but keep the first/carried identity if it does).
        merged->erase(
            std::unique(merged->begin(), merged->end(),
                        [](const LiveEntry& a, const LiveEntry& b) { return a.addr == b.addr; }),
            merged->end());

        // (c) Splice: erase the old window [lo,hi) and insert the merged run at lo. erase/insert shift
        // only the suffix live_[hi,end) — the large, growing prefix live_[0,lo) is never touched, so
        // per-GC cost is O(window + suffix), independent of the total live-set size.
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
    // At shutdown, anything still pending was never collected - i.e. still alive. Append the newly
    // discovered live objects and re-sort once (this is a cold, one-shot path).
    std::size_t appended = 0;
    int n = shardCount_.load(std::memory_order_acquire);
    for (int i = 0; i < n && i < kMaxShards; ++i) {
        Shard* shard = shards_[i];
        if (shard == nullptr)
            continue;
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

std::unordered_map<std::uint64_t, Aggregator::Site> Aggregator::mergeShards() {
    std::unordered_map<std::uint64_t, Site> merged;
    int n = shardCount_.load(std::memory_order_acquire);
    for (int i = 0; i < n && i < kMaxShards; ++i) {
        Shard* shard = shards_[i];
        if (shard == nullptr)
            continue;
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

std::uint32_t Aggregator::internSiteStack(storage::ProvenanceWriter& pw, const Site& site) {
    std::vector<std::string_view> names;
    names.reserve(site.frames.size());
    for (std::size_t i = site.frames.size(); i-- > 0;) { // stored leaf->root; intern root->leaf
        names.push_back(resolveMethodName(site.frames[i]));
    }
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
void Aggregator::emitCorrelation(const std::string& path) {
    storage::ProvenanceWriter pw;

    // Best-effort on a live process: the merge races concurrent record().
    std::unordered_map<std::uint64_t, Site> merged = mergeShards();
    writeProfile(pw, merged);

    // Each live object's address -> its allocation stack id (intern each site once). live_ is kept
    // sorted by address, so this emits an already-sorted object column — the .slab join to the heap
    // dump is then a linear merge-join on address, no read-time sort.
    std::unordered_map<const Site*, std::uint32_t> siteStack;
    for (const LiveEntry& lv : live_) {
        auto [it, inserted] = siteStack.try_emplace(lv.site, 0u);
        if (inserted) {
            it->second = internSiteStack(pw, *lv.site);
        }
        pw.addObject(static_cast<std::uint64_t>(lv.addr), it->second);
    }

    if (!writeSlab(path, pw)) {
        return;
    }
    if (logger_)
        logger_->logInfo("wrote provenance (" + std::to_string(merged.size()) + " stacks, " +
                         std::to_string(live_.size()) + " live objects) to " + path);
}

// Exit-time (or live-flush) allocation aggregate: allocations only, no correlation.
void Aggregator::dump(const std::string& path) {
    std::unordered_map<std::uint64_t, Site> merged = mergeShards();
    storage::ProvenanceWriter pw;
    writeProfile(pw, merged);

    if (!writeSlab(path, pw)) {
        return;
    }
    if (logger_)
        logger_->logInfo("wrote " + std::to_string(merged.size()) + " stacks to " + path);
}

bool Aggregator::writeSlab(const std::string& path, const storage::ProvenanceWriter& pw) {
    storage::ContainerWriter cw;
    pw.writeTo(cw);
    const std::string bytes = cw.finish();
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    if (!out) {
        if (logger_)
            logger_->logError("could not open profile output: " + path);
        return false;
    }
    out.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
    return static_cast<bool>(out);
}

const std::string& Aggregator::resolveMethodName(FunctionID method) {
    auto cached = nameCache_.find(method);
    if (cached != nameCache_.end())
        return cached->second;

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

    return nameCache_.emplace(method, std::move(name)).first->second;
}

const std::string& Aggregator::resolveTypeName(ClassID classId) {
    auto cached = typeNameCache_.find(classId);
    if (cached != typeNameCache_.end())
        return cached->second;

    std::string name = "<unknown>";
    if (classId != 0 && info_ != nullptr) {
        ModuleID moduleId = 0;
        mdTypeDef typeDef = 0;
        if (SUCCEEDED(info_->GetClassIDInfo(classId, &moduleId, &typeDef))) {
            IMetaDataImport* md = nullptr;
            if (SUCCEEDED(info_->GetModuleMetaData(moduleId, ofRead, IID_IMetaDataImport, (IUnknown**)&md)) && md != nullptr) {
                WCHAR typeName16[512];
                ULONG typeLen = 0;
                DWORD typeFlags = 0;
                if (SUCCEEDED(md->GetTypeDefProps(typeDef, typeName16, 512, &typeLen, &typeFlags, nullptr)))
                    name = narrow(typeName16, typeLen);
                md->Release();
            }
        }
    }

    return typeNameCache_.emplace(classId, std::move(name)).first->second;
}

} // namespace Sherlock
