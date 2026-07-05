#pragma once

#include <atomic>
#include <cstdint>
#include <functional>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

#include "profilercommon.h"
#include "sherlock/common/spsc_queue.hpp"

namespace Sherlock {

class Logger;

/// Per-method call tracer driven by the CLR Enter/Leave hooks.
///
/// The hooks are pure producers: each managed thread pushes a compact event into
/// its own SPSC ring (wait-free, no shared cache line on the hot path). A single
/// background consumer drains every ring, replays each thread's stream through a
/// shadow stack, and accumulates inclusive/exclusive time per method. Symbolizing
/// is deferred to dump time, off the consumer's steady-state path.
class TraceCollector {
public:
    TraceCollector(Logger* logger, std::size_t ringCapacity = 1u << 16);
    ~TraceCollector();

    // Hot path — called from the ELT hooks on managed threads.
    void onEnter(FunctionID func);
    void onLeave(FunctionID func);

    void start();  // launch the consumer
    void stop();   // stop the consumer and drain what remains

    /// Writes per-method timings (sorted by exclusive time) using the supplied symbolizer.
    void dump(const std::string& path, const std::function<std::string(FunctionID)>& symbolize);

private:
    // funcId is pointer-aligned, so bit 0 is free to carry the enter/leave flag.
    struct Event {
        std::uint64_t funcAndKind;
        std::uint64_t ts; // nanoseconds, monotonic
    };

    struct Frame {
        FunctionID func;
        std::uint64_t enterTs;
        std::uint64_t childIncl; // summed inclusive time of direct callees
    };

    // A distinct call path (root -> leaf) and its self time, folded so it exports
    // straight to a flamegraph / speedscope.
    struct PathStat {
        std::vector<FunctionID> frames;
        std::uint64_t excl = 0;
        std::uint64_t count = 0;
    };

    // One per managed thread: its ring (producer = that thread, consumer = the
    // drain thread) plus consumer-owned reconstruction state.
    struct ThreadTrace {
        explicit ThreadTrace(std::size_t cap) : ring(cap) {}
        SpscQueue<Event> ring;
        std::vector<Frame> stack;        // consumer-only
        std::atomic<std::uint64_t> dropped{0};
    };

    static constexpr int kMaxThreads = 1024;

    ThreadTrace& localTrace();
    bool drain(ThreadTrace& t);          // consumer: replay events, returns true if any
    void consumeLoop();

    static thread_local ThreadTrace* tls_; // this thread's ring

    Logger* logger_;
    std::size_t ringCapacity_;
    std::atomic<bool> running_{false};

    std::atomic<int> threadCount_{0};
    ThreadTrace* threads_[kMaxThreads] = {};

    std::thread consumer_;
    std::unordered_map<std::uint64_t, PathStat> agg_; // consumer-only, keyed by path hash
};

} // namespace Sherlock
