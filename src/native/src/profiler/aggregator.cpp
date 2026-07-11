#include "sherlock/profiler/aggregator.hpp"

#include "sherlock/common/logger.hpp"
#include "sherlock/storage/profile.hpp"

#include <algorithm>
#include <fstream>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace Sherlock {

namespace {

// One shard pointer per thread. There is a single Aggregator per process, so a
// file-scope thread_local is sufficient (and keeps the hot path branch-free
// after the first allocation on a thread).
thread_local Aggregator::Shard* t_shard = nullptr;

// FNV-1a over the frame ids — cheap and good enough to key distinct stacks.
std::uint64_t hashFrames(const std::vector<FunctionID>& frames) {
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
// map rehash or a pending-vector realloc mid-allocation — bounded latency on the
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
        // else: too many threads — shard still works locally, just isn't dumped.
        t_shard = shard;
    }
    return *t_shard;
}

void Aggregator::record(const std::vector<FunctionID>& frames, std::uint64_t bytes, ObjectID addr) {
    std::uint64_t key = hashFrames(frames);
    Shard& shard = localShard();

    auto it = shard.sites.find(key);
    Site* site;
    if (it == shard.sites.end()) {
        Site fresh;
        fresh.frames = frames;
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
}

void Aggregator::noteSurvivorRange(ObjectID start, std::uint64_t length) {
    survivorRanges_.emplace_back(static_cast<std::uint64_t>(start),
                                 static_cast<std::uint64_t>(start) + length);
}

void Aggregator::noteMove(ObjectID oldStart, ObjectID newStart, std::uint64_t length) {
    // The old range is also a survivor span (for the liveness test); the old->new
    // delta additionally lets us follow the object's identity to its new address.
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

void Aggregator::endGc() {
    std::sort(survivorRanges_.begin(), survivorRanges_.end());
    if (correlate_)
        std::sort(moves_.begin(), moves_.end(),
                  [](const intervals::MoveRange& a, const intervals::MoveRange& b) { return a.oldStart < b.oldStart; });

    // Rebuild the live set: survivors carried over (identity + remapped address) plus
    // objects allocated since the last GC that survived this one (fresh identity).
    std::unordered_map<ObjectID, Live> next;
    if (correlate_) {
        next.reserve(live_.size()); // avoid rehashing mid-rebuild (extends the GC pause)
        for (const auto& [addr, lv] : live_)
            if (survived(addr))
                next.insert_or_assign(remap(addr), lv);
    }

    int n = shardCount_.load(std::memory_order_acquire);
    for (int i = 0; i < n && i < kMaxShards; ++i) {
        Shard* shard = shards_[i];
        if (shard == nullptr)
            continue;
        for (const Pending& p : shard->pending) {
            if (survived(p.addr)) {
                p.site->survived.count += 1;
                p.site->survived.bytes += p.bytes;
                if (correlate_) {
                    ObjectID a = remap(p.addr);
                    if (!next.contains(a))
                        next.insert_or_assign(a, Live{nextObjectId_.fetch_add(1), p.site});
                }
            }
        }
        shard->pending.clear();
    }

    if (correlate_)
        live_ = std::move(next);
    survivorRanges_.clear();
    moves_.clear();
}

void Aggregator::countPendingAsSurvived() {
    // At shutdown, anything still pending was never collected — i.e. still alive.
    int n = shardCount_.load(std::memory_order_acquire);
    for (int i = 0; i < n && i < kMaxShards; ++i) {
        Shard* shard = shards_[i];
        if (shard == nullptr)
            continue;
        for (const Pending& p : shard->pending) {
            p.site->survived.count += 1;
            p.site->survived.bytes += p.bytes;
            if (correlate_ && !live_.contains(p.addr))
                live_.insert_or_assign(p.addr, Live{nextObjectId_.fetch_add(1), p.site});
        }
        shard->pending.clear();
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

void Aggregator::emitCorrelation(const std::string& path) {
    // A snapshot's unified provenance.slab: the allocation profile AND per-object correlation,
    // sharing ONE interned stack table (one identity space — dedup across the profile and the
    // live objects). sl joins the correlation to a heap dump by address (see storage-format.md).
    storage::ProvenanceWriter pw;

    // Allocation profile (best-effort merge on a live process — same race as a live flush).
    std::unordered_map<std::uint64_t, Site> merged = mergeShards();
    for (const auto& [key, site] : merged) {
        const std::uint32_t stackId = internSiteStack(pw, site);
        pw.addAllocation(stackId, site.alloc.bytes, site.alloc.count, site.survived.bytes, site.survived.count);
    }

    // Correlation: each live object → its allocation stack id (intern each site once).
    std::unordered_map<const Site*, std::uint32_t> siteStack;
    for (const auto& [addr, lv] : live_) {
        auto [it, inserted] = siteStack.try_emplace(lv.site, 0u);
        if (inserted) {
            it->second = internSiteStack(pw, *lv.site);
        }
        pw.addObject(static_cast<std::uint64_t>(addr), it->second);
    }

    if (!writeSlab(path, pw)) {
        return;
    }
    if (logger_)
        logger_->logInfo("wrote provenance (" + std::to_string(merged.size()) + " stacks, " +
                         std::to_string(live_.size()) + " live objects) to " + path);
}

void Aggregator::dump(const std::string& path) {
    // Exit-time (or live-flush) allocation aggregate — allocations only, no correlation.
    std::unordered_map<std::uint64_t, Site> merged = mergeShards();
    storage::ProvenanceWriter pw;
    for (const auto& [key, site] : merged) {
        const std::uint32_t stackId = internSiteStack(pw, site);
        pw.addAllocation(stackId, site.alloc.bytes, site.alloc.count, site.survived.bytes, site.survived.count);
    }

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
