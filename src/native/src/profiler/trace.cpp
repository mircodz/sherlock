#include "sherlock/profiler/trace.hpp"

#include "sherlock/common/logger.hpp"

#include <algorithm>
#include <chrono>
#include <fstream>

namespace Sherlock {

// Per-thread handle to this thread's ring (single TraceCollector per process).
thread_local TraceCollector::ThreadTrace* TraceCollector::tls_ = nullptr;

namespace {

// Reentrancy guard: our hook can itself trigger instrumented managed code (e.g. the
// first-call ring allocation), which would re-enter the hook and recurse until the
// stack overflows. While we're inside the hook on this thread, ignore nested calls.
thread_local bool t_inHook = false;

struct HookGuard {
    bool entered;
    HookGuard() : entered(!t_inHook) { t_inHook = true; }
    ~HookGuard() { if (entered) t_inHook = false; }
};

std::uint64_t nowNs() {
    return static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
}

constexpr std::uint64_t kLeaveBit = 1ull;

} // namespace

TraceCollector::TraceCollector(Logger* logger, std::size_t ringCapacity)
    : logger_(logger), ringCapacity_(ringCapacity) {
}

TraceCollector::~TraceCollector() {
    stop();
    int n = threadCount_.load(std::memory_order_acquire);
    for (int i = 0; i < n && i < kMaxThreads; ++i) {
        delete threads_[i];
    }
}

TraceCollector::ThreadTrace& TraceCollector::localTrace() {
    if (tls_ == nullptr) {
        auto* tt = new ThreadTrace(ringCapacity_);
        int idx = threadCount_.fetch_add(1, std::memory_order_acq_rel);
        if (idx < kMaxThreads) {
            threads_[idx] = tt;
        }
        tls_ = tt;
    }
    return *tls_;
}

void TraceCollector::onEnter(FunctionID func) {
    if (!running_.load(std::memory_order_relaxed) || t_inHook) {
        return;
    }

    HookGuard guard;
    ThreadTrace& tt = localTrace();
    Event e{static_cast<std::uint64_t>(func) & ~kLeaveBit, nowNs()};

    if (!tt.ring.push(e)) {
        tt.dropped.fetch_add(1, std::memory_order_relaxed);
    }
}

void TraceCollector::onLeave(FunctionID func) {
    if (!running_.load(std::memory_order_relaxed) || t_inHook) {
        return;
    }

    HookGuard guard;
    ThreadTrace& tt = localTrace();
    Event e{(static_cast<std::uint64_t>(func) & ~kLeaveBit) | kLeaveBit, nowNs()};

    if (!tt.ring.push(e)) {
        tt.dropped.fetch_add(1, std::memory_order_relaxed);
    }
}

bool TraceCollector::drain(ThreadTrace& tt) {
    bool any = false;
    Event e;

    // Bounded per-pass so one busy thread can't starve the others.
    for (int i = 0; i < 8192 && tt.ring.pop(e); ++i) {
        any = true;
        bool leave = (e.funcAndKind & kLeaveBit) != 0;
        auto func = static_cast<FunctionID>(e.funcAndKind & ~kLeaveBit);

        if (!leave) {
            tt.stack.push_back({func, e.ts, 0});
            continue;
        }

        if (tt.stack.empty()) {
            continue; // a dropped enter left us unbalanced; skip
        }

        Frame top = tt.stack.back();
        tt.stack.pop_back();
        std::uint64_t incl = e.ts - top.enterTs;
        std::uint64_t excl = incl - top.childIncl;

        // Fold the exclusive time onto this exact call path (parents + this frame),
        // root -> leaf. Hash the path (FNV-1a) to key the aggregate.
        std::uint64_t h = 1469598103934665603ull;
        for (const Frame& f : tt.stack) {
            h ^= static_cast<std::uint64_t>(f.func);
            h *= 1099511628211ull;
        }
        h ^= static_cast<std::uint64_t>(top.func);
        h *= 1099511628211ull;

        PathStat& ps = agg_[h];
        ps.excl += excl;
        ps.count += 1;
        if (ps.frames.empty()) {
            ps.frames.reserve(tt.stack.size() + 1);
            for (const Frame& f : tt.stack) {
                ps.frames.push_back(f.func);
            }
            ps.frames.push_back(top.func);
        }

        if (!tt.stack.empty())
            tt.stack.back().childIncl += incl;
    }
    return any;
}

void TraceCollector::consumeLoop() {
    while (running_.load(std::memory_order_acquire)) {
        bool any = false;
        int n = threadCount_.load(std::memory_order_acquire);
        for (int i = 0; i < n && i < kMaxThreads; ++i) {
            ThreadTrace* tt = threads_[i];
            if (tt != nullptr)
                any |= drain(*tt);
        }
        if (!any) {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    }
}

void TraceCollector::start() {
    running_.store(true, std::memory_order_release);
    consumer_ = std::thread([this] { consumeLoop(); });
}

void TraceCollector::stop() {
    if (!running_.exchange(false)) {
        return;
    }

    if (consumer_.joinable()) {
        consumer_.join();
    }

    // Final pass: producers have stopped, drain whatever's left.
    int n = threadCount_.load(std::memory_order_acquire);
    for (int i = 0; i < n && i < kMaxThreads; ++i) {
        if (threads_[i] != nullptr) {
            drain(*threads_[i]);
        }
    }
}

void TraceCollector::dump(const std::string& path, const std::function<std::string(FunctionID)>& symbolize) {
    std::vector<const PathStat*> rows;
    rows.reserve(agg_.size());
    for (const auto& [h, ps] : agg_) {
        rows.push_back(&ps);
    }
    std::sort(rows.begin(), rows.end(), [](const PathStat* a, const PathStat* b) { return a->excl > b->excl; });

    std::ofstream out(path, std::ios::trunc);
    if (!out) {
        if (logger_) {
            logger_->logError("could not open trace output: " + path);
        }
        return;
    }

    std::uint64_t dropped = 0;
    int n = threadCount_.load(std::memory_order_acquire);
    for (int i = 0; i < n && i < kMaxThreads; ++i) {
        if (threads_[i] != nullptr) {
            dropped += threads_[i]->dropped.load(std::memory_order_relaxed);
        }
    }

    // Folded stacks weighted by exclusive (self) time - feeds speedscope/flamegraph.
    out << "# sherlock method trace (folded stacks, exclusive nanoseconds)\n";
    out << "# exclusive_ns\tcount\tstack\n";
    for (const PathStat* ps : rows) {
        out << ps->excl << '\t' << ps->count << '\t';
        for (std::size_t i = 0; i < ps->frames.size(); ++i) {
            out << symbolize(ps->frames[i]); // already root -> leaf
            if (i + 1 < ps->frames.size()) {
                out << ';';
            }
        }
        out << '\n';
    }

    if (logger_) {
        logger_->logInfo("wrote " + std::to_string(rows.size()) + " call paths to " + path +
                         (dropped ? " (" + std::to_string(dropped) + " events dropped — ring too small)" : ""));
    }
}

} // namespace Sherlock
