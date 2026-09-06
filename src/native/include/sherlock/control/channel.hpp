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

// Windows SOCKET is pointer-width with an all-bits-set invalid value; int would truncate it.
// Keep Windows headers out of this file so channel.cpp controls Winsock include order.
#ifdef _WIN32
using SocketHandle = std::uintptr_t;
#else
using SocketHandle = int;
#endif
inline constexpr SocketHandle kInvalidSocket = static_cast<SocketHandle>(-1); // matches INVALID_SOCKET's bit pattern too

struct Reply {
    bool ok = true;
    std::string detail;

    static Reply success(std::string detail = {}) { return {true, std::move(detail)}; }
    static Reply error(std::string message) { return {false, std::move(message)}; }
};

// One AF_UNIX client per process. Serves sl requests on a native thread and pushes events.
// Wire format is defined in protocol.hpp.
class ControlChannel {
public:
    // Runs on the native reader thread, outside CLR callbacks.
    using Handler = std::function<Reply(std::string_view cmd, std::span<const std::string_view> args)>;
    using DisconnectHandler = std::function<void()>;

    explicit ControlChannel(Logger* logger);
    ~ControlChannel();

    ControlChannel(const ControlChannel&) = delete;
    ControlChannel& operator=(const ControlChannel&) = delete;

    // nullopt on success, otherwise an error message.
    [[nodiscard]] std::optional<std::string> connect(const std::string& socketPath);

    // Send HELLO before serving requests.
    void start(
        std::string_view version,
        const std::vector<std::string>& features,
        Handler handler,
        DisconnectHandler disconnected = {});

    // Thread-safe; fields follow the EVENT verb.
    [[nodiscard]] bool sendEvent(const std::vector<std::string>& fields);

    void stop();

    [[nodiscard]] bool connected() const {
        return running_.load(std::memory_order_acquire) &&
               fd_.load(std::memory_order_acquire) != kInvalidSocket;
    }

private:
    void serve();
    bool sendAll(std::span<const char> bytes);

    Logger* logger_;
    // Concurrent readers observe stop() resetting the handle.
    std::atomic<SocketHandle> fd_{kInvalidSocket};
    Handler handler_;
    DisconnectHandler disconnected_;
    std::atomic<bool> running_{false};
    std::thread worker_;
    std::mutex writeMutex_; // also serializes stop()'s close() against an in-flight sendAll()
#ifdef _WIN32
    bool wsaOwned_ = false; // true once this instance has bumped the process-wide WSAStartup refcount
#endif
};

} // namespace Sherlock::control
