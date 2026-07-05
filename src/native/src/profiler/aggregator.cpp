#include "sherlock/profiler/aggregator.hpp"

#include "sherlock/common/logger.hpp"

#include <algorithm>
#include <fstream>

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

Aggregator::Shard& Aggregator::localShard() {
    if (t_shard == nullptr) {
        auto* shard = new Shard();
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

void Aggregator::emitCorrelation(const std::string& path) {
    std::ofstream out(path, std::ios::trunc);
    if (!out) {
        if (logger_)
            logger_->logError("could not open correlation output: " + path);
        return;
    }

    // Path A join file: every live tracked object at its current address, with the
    // allocation call stack. sl joins this to a heap dump by address (see docs).
    out << "# sherlock correlation (live object -> allocation stack)\n";
    out << "# address\tobject_id\tstack\n";
    for (const auto& [addr, lv] : live_) {
        out << "0x" << std::hex << addr << std::dec << '\t' << lv.id << '\t';
        const std::vector<FunctionID>& frames = lv.site->frames;
        if (frames.empty()) {
            out << "<no managed frame>";
        } else {
            for (std::size_t i = frames.size(); i-- > 0;) {
                out << resolveMethodName(frames[i]);
                if (i != 0)
                    out << ';';
            }
        }
        out << '\n';
    }

    if (logger_)
        logger_->logInfo("wrote " + std::to_string(live_.size()) + " live objects to " + path);
}

void Aggregator::dump(const std::string& path) {
    // Merge all shards by stack. Safe without locking: the caller guarantees
    // allocations have stopped (profiler is shutting down).
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

    std::vector<const Site*> rows;
    rows.reserve(merged.size());
    for (const auto& [key, site] : merged)
        rows.push_back(&site);
    std::sort(rows.begin(), rows.end(),
              [](const Site* a, const Site* b) { return a->alloc.bytes > b->alloc.bytes; });

    std::ofstream out(path, std::ios::trunc);
    if (!out) {
        if (logger_)
            logger_->logError("could not open profile output: " + path);
        return;
    }

    out << "# sherlock allocation profile (folded stacks, root->leaf)\n";
    out << "# alloc_bytes\talloc_count\tsurvived_bytes\tsurvived_count\tstack\n";
    for (const Site* site : rows) {
        out << site->alloc.bytes << '\t' << site->alloc.count << '\t'
            << site->survived.bytes << '\t' << site->survived.count << '\t';
        if (site->frames.empty()) {
            out << "<no managed frame>";
        } else {
            // Stored leaf -> root; emit root -> leaf so callers come first.
            for (std::size_t i = site->frames.size(); i-- > 0;) {
                out << resolveMethodName(site->frames[i]);
                if (i != 0)
                    out << ';';
            }
        }
        out << '\n';
    }

    if (logger_)
        logger_->logInfo("wrote " + std::to_string(rows.size()) + " stacks to " + path);
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
