#pragma once

#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

namespace Sherlock {
class Logger;
}

namespace Sherlock::control {

// Storage for the OS socket handle. Windows' SOCKET is an unsigned, pointer-width handle (not a
// small int, and its invalid value is all-bits-set rather than -1), so it can't be stored in an
// `int` without truncation/UB on 64-bit. We use a plain uintptr_t here (rather than <winsock2.h>'s
// SOCKET) so this header never pulls in Windows headers - channel.cpp owns the winsock2.h/windows.h
// include order.
#ifdef _WIN32
using SocketHandle = std::uintptr_t;
#else
using SocketHandle = int;
#endif
inline constexpr SocketHandle kInvalidSocket = static_cast<SocketHandle>(-1); // matches INVALID_SOCKET's bit pattern too

/// A handler's answer to a request: ok + optional detail, or an error message.
struct Reply {
    bool ok = true;
    std::string detail;

    static Reply success(std::string detail = {}) { return {true, std::move(detail)}; }
    static Reply error(std::string message) { return {false, std::move(message)}; }
};

/// The profiler side of the sl <-> profiler control channel: a Unix-domain-socket client
/// that connects to sl, announces its capabilities, serves requests on a background
/// thread, and can push unsolicited events (e.g. probe hits). See protocol.hpp for the
/// wire format. One channel per process.
class ControlChannel {
public:
    /// cmd + args -> Reply. Runs on the channel's reader thread (a native thread, so it's
    /// safe to call ForceGC etc. from here).
    using Handler = std::function<Reply(std::string_view cmd, std::span<const std::string_view> args)>;

    explicit ControlChannel(Logger* logger);
    ~ControlChannel();

    ControlChannel(const ControlChannel&) = delete;
    ControlChannel& operator=(const ControlChannel&) = delete;

    /// Connects to sl's listening socket at `socketPath`. Returns nullopt on success, or
    /// an error message on failure. (std::optional rather than std::expected for portable
    /// C++23 - libstdc++ on some CI toolchains lacks <expected>.)
    [[nodiscard]] std::optional<std::string> connect(const std::string& socketPath);

    /// Sends HELLO (version + features), then serves requests on a background thread.
    void start(std::string_view version, const std::vector<std::string>& features, Handler handler);

    /// Pushes an unsolicited EVENT frame to sl (fields after the "EVENT" verb). Thread-safe.
    void sendEvent(const std::vector<std::string>& fields);

    void stop();

    [[nodiscard]] bool connected() const { return fd_.load(std::memory_order_acquire) != kInvalidSocket; }

private:
    void serve();
    bool sendAll(std::span<const char> bytes); // best-effort; callers ignore the result

    Logger* logger_;
    // The socket handle is read on the worker thread (serve/sendAll) and by connected() from other
    // threads, while stop() closes and resets it — make it atomic to avoid a torn/racy read.
    std::atomic<SocketHandle> fd_{kInvalidSocket};
    Handler handler_;
    std::atomic<bool> running_{false};
    std::thread worker_;
    std::mutex writeMutex_; // also serializes stop()'s close() against an in-flight sendAll()
#ifdef _WIN32
    bool wsaOwned_ = false; // true once this instance has bumped the process-wide WSAStartup refcount
#endif
};

} // namespace Sherlock::control
