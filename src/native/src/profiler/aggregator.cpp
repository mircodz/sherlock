#include "sherlock/profiler/aggregator.hpp"

#include "sherlock/common/logger.hpp"

#include <algorithm>
#include <fstream>
#include <utility>

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
    for (Shard* shard : shards_)
        delete shard;
}

Aggregator::Shard& Aggregator::localShard() {
    if (t_shard == nullptr) {
        auto* shard = new Shard();
        {
            std::lock_guard<std::mutex> lock(shardsMutex_);
            shards_.push_back(shard);
        }
        t_shard = shard;
    }
    return *t_shard;
}

void Aggregator::record(const std::vector<FunctionID>& frames, std::uint64_t bytes) {
    std::uint64_t key = hashFrames(frames);
    auto& sites = localShard().sites;

    auto it = sites.find(key);
    if (it == sites.end()) {
        // First time we've seen this stack on this thread: store it once.
        Site site;
        site.frames = frames;
        site.stats.count = 1;
        site.stats.bytes = bytes;
        sites.emplace(key, std::move(site));
    } else {
        it->second.stats.count += 1;
        it->second.stats.bytes += bytes;
    }
}

void Aggregator::dump(const std::string& path) {
    // Merge all shards by stack. Safe without locking the shards themselves: the
    // caller guarantees allocations have stopped (profiler is shutting down).
    std::unordered_map<std::uint64_t, Site> merged;
    {
        std::lock_guard<std::mutex> lock(shardsMutex_);
        for (Shard* shard : shards_) {
            for (auto& [key, site] : shard->sites) {
                auto it = merged.find(key);
                if (it == merged.end()) {
                    merged.emplace(key, site);
                } else {
                    it->second.stats.count += site.stats.count;
                    it->second.stats.bytes += site.stats.bytes;
                }
            }
        }
    }

    std::vector<const Site*> rows;
    rows.reserve(merged.size());
    for (const auto& [key, site] : merged)
        rows.push_back(&site);
    std::sort(rows.begin(), rows.end(),
              [](const Site* a, const Site* b) { return a->stats.bytes > b->stats.bytes; });

    std::ofstream out(path, std::ios::trunc);
    if (!out) {
        if (logger_)
            logger_->logError("could not open profile output: " + path);
        return;
    }

    out << "# sherlock allocation profile (folded stacks, root->leaf)\n";
    out << "# bytes\tcount\tstack\n";
    for (const Site* site : rows) {
        out << site->stats.bytes << '\t' << site->stats.count << '\t';
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

} // namespace Sherlock
