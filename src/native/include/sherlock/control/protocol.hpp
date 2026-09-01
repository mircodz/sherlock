#pragma once

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

// Wire framing + message helpers for the sl <-> profiler control channel.
//
// A message is a 4-byte little-endian length followed by a UTF-8 payload. The payload
// is tab-separated fields; the first field is the verb:
//   HELLO \t <version> \t <comma,separated,features> \t <pid>   profiler -> sl on connect
//   REQ   \t <id> \t <command> [\t args...]                     sl -> profiler
//   RES   \t <id> \t ok|err    [\t detail]                      profiler -> sl
//   EVENT \t <name> [\t args...]                                profiler -> sl (unsolicited)
//
namespace Sherlock::control {

/// Control command verbs (payload of a REQ frame). Mirrored on the C# side in
/// ControlCommands so both ends agree; keep the two in sync.
namespace commands {
inline constexpr std::string_view kPing = "ping";
inline constexpr std::string_view kEmitCorrelation = "emit-correlation";
inline constexpr std::string_view kFlushAllocations = "flush-allocations";
inline constexpr std::string_view kArmTrigger = "arm-trigger";
inline constexpr std::string_view kGcCount = "gc-count";
inline constexpr std::string_view kHeapSize = "heap-size";

// EXPERIMENTAL: snapshot correlation while GarbageCollectionFinished keeps the heap still.
inline constexpr std::string_view kBeginCoherentCapture = "begin-coherent-capture";
inline constexpr std::string_view kCompleteCoherentCapture = "complete-coherent-capture";
inline constexpr std::string_view kAbortCoherentCapture = "abort-coherent-capture";
inline constexpr std::string_view kReleaseExitCapture = "release-exit-capture";
} // namespace commands

/// Event names pushed in an EVENT frame. Mirrored on the C# side in ControlEvents.
namespace events {
inline constexpr std::string_view kSnapshotTrigger = "snapshot-trigger";

inline constexpr std::string_view kCoherentCaptureReady = "coherent-capture-ready";
inline constexpr std::string_view kCoherentCaptureFailed = "coherent-capture-failed";
inline constexpr std::string_view kExitCaptureReady = "exit-capture-ready";
} // namespace events

/// Prepends the 4-byte little-endian length to a payload.
[[nodiscard]] inline std::string frame(std::string_view payload) {
    const auto len = static_cast<std::uint32_t>(payload.size());
    std::string out;
    out.reserve(4 + payload.size());
    out.push_back(static_cast<char>(len & 0xFF));
    out.push_back(static_cast<char>((len >> 8) & 0xFF));
    out.push_back(static_cast<char>((len >> 16) & 0xFF));
    out.push_back(static_cast<char>((len >> 24) & 0xFF));
    out.append(payload);
    return out;
}

/// Returns the payload of the next complete frame, consuming it from `buffer`. Returns
/// nullopt (leaving `buffer` intact) when it doesn't yet hold a full frame.
[[nodiscard]] inline std::optional<std::string> tryReadFrame(std::string& buffer) {
    if (buffer.size() < 4) {
        return std::nullopt;
    }
    const std::uint32_t len = static_cast<std::uint8_t>(buffer[0]) |
                              (static_cast<std::uint32_t>(static_cast<std::uint8_t>(buffer[1])) << 8) |
                              (static_cast<std::uint32_t>(static_cast<std::uint8_t>(buffer[2])) << 16) |
                              (static_cast<std::uint32_t>(static_cast<std::uint8_t>(buffer[3])) << 24);
    if (buffer.size() < std::size_t{4} + len) {
        return std::nullopt;
    }
    std::string payload = buffer.substr(4, len);
    buffer.erase(0, std::size_t{4} + len);
    return payload;
}

/// Splits a payload into tab-separated fields (views into `payload`, which must outlive  them). Always returns at least one field.
[[nodiscard]] inline std::vector<std::string_view> splitFields(std::string_view payload) {
    std::vector<std::string_view> fields;
    std::size_t start = 0;
    for (;;) {
        const std::size_t tab = payload.find('\t', start);
        if (tab == std::string_view::npos) {
            fields.push_back(payload.substr(start));
            break;
        }
        fields.push_back(payload.substr(start, tab - start));
        start = tab + 1;
    }
    return fields;
}

/// Joins fields with tabs.
template <typename Range>
[[nodiscard]] inline std::string joinFields(const Range& fields) {
    std::string out;
    bool first = true;
    for (std::string_view f : fields) {
        if (!first) {
            out += '\t';
        }
        out += f;
        first = false;
    }
    return out;
}

/// Single-flight rendezvous between a GC callback and the control thread.
class CoherentCaptureBarrier {
public:
    enum class State { Idle, Arming, Parked };
    enum class ParkResult { Released, TimedOut };

    [[nodiscard]] bool begin(std::string token) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (state_ != State::Idle) {
            return false;
        }
        state_ = State::Arming;
        token_ = std::move(token);
        gcCountAtReady_ = 0;
        released_ = false;
        active_.store(true, std::memory_order_release);
        return true;
    }

    [[nodiscard]] bool abort(const std::string& token) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (state_ == State::Idle || token_ != token || released_) {
            return false;
        }
        if (state_ == State::Arming) {
            state_ = State::Idle;
            token_.clear();
            active_.store(false, std::memory_order_release);
        } else {
            released_ = true;
            cv_.notify_all();
        }
        return true;
    }

    [[nodiscard]] bool active() const noexcept { return active_.load(std::memory_order_acquire); }

    [[nodiscard]] bool markReady(std::uint64_t gcCount) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (state_ != State::Arming) {
            return false;
        }
        state_ = State::Parked;
        gcCountAtReady_ = gcCount;
        // Preserve a shutdown release that raced with the GC while Arming.
        return true;
    }

    [[nodiscard]] ParkResult park(std::chrono::milliseconds timeout) {
        std::unique_lock<std::mutex> lock(mutex_);
        const bool wasReleased = cv_.wait_for(lock, timeout, [this] { return released_; });
        state_ = State::Idle;
        token_.clear();
        released_ = false;
        active_.store(false, std::memory_order_release);
        return wasReleased ? ParkResult::Released : ParkResult::TimedOut;
    }

    [[nodiscard]] bool release(const std::string& token, std::uint64_t& gcCount) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (state_ != State::Parked || token_ != token || released_) {
            return false;
        }
        gcCount = gcCountAtReady_;
        released_ = true;
        cv_.notify_all();
        return true;
    }

    [[nodiscard]] bool isParkedFor(const std::string& token) const {
        std::lock_guard<std::mutex> lock(mutex_);
        return state_ == State::Parked && token_ == token;
    }

    void forceRelease() {
        std::lock_guard<std::mutex> lock(mutex_);
        if (state_ == State::Idle) {
            return;
        }
        released_ = true;
        cv_.notify_all();
    }

    [[nodiscard]] State state() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return state_;
    }

    [[nodiscard]] std::string token() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return token_;
    }

private:
    mutable std::mutex mutex_;
    std::condition_variable cv_;
    State state_ = State::Idle;
    std::string token_;
    std::uint64_t gcCountAtReady_ = 0;
    bool released_ = false;

    // Avoid a mutex on every GC when the experiment is unused.
    std::atomic<bool> active_{false};
};

/// Holds a normal entry-point return until the supervisor finishes capturing.
class ExitCaptureLatch {
public:
    enum class WaitResult { Released, TimedOut };

    [[nodiscard]] bool begin(std::string token) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (active_) {
            return false;
        }
        token_ = std::move(token);
        released_ = false;
        active_ = true;
        return true;
    }

    [[nodiscard]] WaitResult wait(std::chrono::milliseconds timeout) {
        std::unique_lock<std::mutex> lock(mutex_);
        const bool released = cv_.wait_for(lock, timeout, [this] { return released_; });
        token_.clear();
        released_ = false;
        active_ = false;
        return released ? WaitResult::Released : WaitResult::TimedOut;
    }

    [[nodiscard]] bool release(const std::string& token) {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!active_ || released_ || token_ != token) {
            return false;
        }
        released_ = true;
        cv_.notify_all();
        return true;
    }

    void forceRelease() {
        std::lock_guard<std::mutex> lock(mutex_);
        if (active_) {
            released_ = true;
            cv_.notify_all();
        }
    }

    [[nodiscard]] bool active() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return active_;
    }

private:
    mutable std::mutex mutex_;
    std::condition_variable cv_;
    std::string token_;
    bool active_ = false;
    bool released_ = false;
};

} // namespace Sherlock::control
