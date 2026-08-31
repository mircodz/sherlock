#include "sherlock/control/channel.hpp"

#include "sherlock/common/logger.hpp"
#include "sherlock/control/protocol.hpp"

#include <algorithm>
#include <cstring>
#include <exception>
#include <limits>
#include <utility>

#ifdef _WIN32
// winsock2.h must precede windows.h in this translation unit: windows.h pulls in the legacy
// winsock.h unless _WINSOCKAPI_ is already defined, and winsock2.h/winsock.h can't coexist.
// This file never includes the CLR profiler headers (which define their own COM_NO_WINDOWS_H /
// shim windows.h for the Unix build), so we're free to pick this order here.
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
#include <afunix.h> // sockaddr_un for AF_UNIX sockets (Windows 10 1803+ / SDK 10.0.17763+)
#include <windows.h>
#include <process.h> // _getpid
#else
#include <cerrno>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>
#endif

namespace Sherlock::control {

namespace {
#ifdef MSG_NOSIGNAL
constexpr int kSendFlags = MSG_NOSIGNAL; // Linux: don't raise SIGPIPE on a closed peer
#else
constexpr int kSendFlags = 0;            // macOS uses SO_NOSIGPIPE (set below); Windows never raises SIGPIPE
#endif

#ifdef _WIN32
// WSAStartup/WSACleanup are process-wide and must be balanced; ref-count across ControlChannel
// instances so the first connect() initializes Winsock and the last stop()/destructor tears it down.
std::mutex& wsaMutex() {
    static std::mutex m;
    return m;
}
int g_wsaRefCount = 0;

std::string winsockError(int code) {
    char buf[256];
    DWORD n = ::FormatMessageA(
        FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, nullptr,
        static_cast<DWORD>(code), 0, buf, sizeof buf, nullptr);
    while (n > 0 && (buf[n - 1] == '\n' || buf[n - 1] == '\r')) {
        --n; // FormatMessage appends a trailing CRLF
    }
    std::string message = n > 0 ? std::string(buf, n) : std::string("unknown error");
    return message + " (WSA " + std::to_string(code) + ")";
}

// Bumps the process-wide WSAStartup refcount, initializing Winsock on the first call. Returns an
// error message on failure.
std::optional<std::string> wsaAcquire() {
    std::lock_guard<std::mutex> lock(wsaMutex());
    if (g_wsaRefCount > 0) {
        ++g_wsaRefCount;
        return std::nullopt;
    }
    WSADATA data;
    int rc = WSAStartup(MAKEWORD(2, 2), &data);
    if (rc != 0) {
        return "WSAStartup failed: " + winsockError(rc);
    }
    g_wsaRefCount = 1;
    return std::nullopt;
}

// Drops the process-wide WSAStartup refcount, calling WSACleanup once the last channel releases it.
void wsaRelease() {
    std::lock_guard<std::mutex> lock(wsaMutex());
    if (g_wsaRefCount > 0 && --g_wsaRefCount == 0) {
        ::WSACleanup();
    }
}
#endif // _WIN32

void shutdownSocket(SocketHandle handle) {
#ifdef _WIN32
    ::shutdown(static_cast<SOCKET>(handle), SD_BOTH);
#else
    ::shutdown(handle, SHUT_RDWR);
#endif
}

void closeSocket(SocketHandle handle) {
#ifdef _WIN32
    ::closesocket(static_cast<SOCKET>(handle));
#else
    ::close(handle);
#endif
}

// Reads up to `len` bytes. Returns bytes read (>0), 0 on an orderly close, or -1 on error.
// Retries transparently on an interrupted call.
long long recvSome(SocketHandle handle, char* buf, std::size_t len) {
#ifdef _WIN32
    const SOCKET fd = static_cast<SOCKET>(handle);
    const int chunk = static_cast<int>(std::min<std::size_t>(len, std::numeric_limits<int>::max()));
    for (;;) {
        int n = ::recv(fd, buf, chunk, 0);
        if (n != SOCKET_ERROR) {
            return n;
        }
        if (WSAGetLastError() == WSAEINTR) {
            continue; // interrupted — retry rather than drop the connection
        }
        return -1;
    }
#else
    for (;;) {
        ssize_t n = ::recv(handle, buf, len, 0);
        if (n < 0 && errno == EINTR) {
            continue; // interrupted by a signal — retry rather than drop the connection
        }
        return n;
    }
#endif
}

// Sends up to `len` bytes in one call. Returns bytes sent (>0), or -1 on error. Retries
// transparently on an interrupted call.
long long sendSome(SocketHandle handle, const char* buf, std::size_t len) {
#ifdef _WIN32
    const SOCKET fd = static_cast<SOCKET>(handle);
    const int chunk = static_cast<int>(std::min<std::size_t>(len, std::numeric_limits<int>::max()));
    for (;;) {
        int n = ::send(fd, buf, chunk, 0);
        if (n != SOCKET_ERROR) {
            return n;
        }
        if (WSAGetLastError() == WSAEINTR) {
            continue; // interrupted — retry
        }
        return -1;
    }
#else
    for (;;) {
        ssize_t n = ::send(handle, buf, len, kSendFlags);
        if (n < 0 && errno == EINTR) {
            continue; // interrupted — retry
        }
        return n;
    }
#endif
}
} // namespace

ControlChannel::ControlChannel(Logger* logger) : logger_(logger) {}

ControlChannel::~ControlChannel() {
    stop();
}

std::optional<std::string> ControlChannel::connect(const std::string& socketPath) {
#ifdef _WIN32
    if (std::optional<std::string> err = wsaAcquire()) {
        return err;
    }
    wsaOwned_ = true;

    SOCKET sock = ::socket(AF_UNIX, SOCK_STREAM, 0);
    if (sock == INVALID_SOCKET) {
        std::string err = "socket() failed: " + winsockError(WSAGetLastError());
        wsaRelease();
        wsaOwned_ = false;
        return err;
    }

    sockaddr_un addr{};
    addr.sun_family = AF_UNIX;
    if (socketPath.size() >= sizeof(addr.sun_path)) {
        ::closesocket(sock);
        wsaRelease();
        wsaOwned_ = false;
        return "socket path too long: " + socketPath;
    }
    std::memcpy(addr.sun_path, socketPath.c_str(), socketPath.size() + 1);

    if (::connect(sock, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR) {
        std::string err = "connect() failed: " + winsockError(WSAGetLastError());
        ::closesocket(sock);
        wsaRelease();
        wsaOwned_ = false;
        return err;
    }

    fd_.store(static_cast<SocketHandle>(sock), std::memory_order_release);
    return std::nullopt;
#else
    int fd = ::socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0) {
        return std::string("socket() failed: ") + std::strerror(errno);
    }

#ifdef SO_NOSIGPIPE
    int one = 1;
    ::setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, sizeof one);
#endif

    sockaddr_un addr{};
    addr.sun_family = AF_UNIX;
    if (socketPath.size() >= sizeof(addr.sun_path)) {
        ::close(fd);
        return "socket path too long: " + socketPath;
    }
    std::memcpy(addr.sun_path, socketPath.c_str(), socketPath.size() + 1);

    if (::connect(fd, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) != 0) {
        std::string err = std::strerror(errno);
        ::close(fd);
        return "connect() failed: " + err;
    }

    fd_.store(fd, std::memory_order_release);
    return std::nullopt;
#endif
}

void ControlChannel::start(
    std::string_view version,
    const std::vector<std::string>& features,
    Handler handler,
    DisconnectHandler disconnected) {
    if (fd_.load(std::memory_order_acquire) == kInvalidSocket) {
        return;
    }
    handler_ = std::move(handler);
    disconnected_ = std::move(disconnected);

    std::string featureList;
    for (std::size_t i = 0; i < features.size(); ++i) {
        if (i != 0) featureList += ',';
        featureList += features[i];
    }
    // Identify ourselves by pid so sl can address this specific process on a shared socket
    // (a whole `dotnet run` subtree connects to one control socket).
#ifdef _WIN32
    const int pid = _getpid();
#else
    const int pid = ::getpid();
#endif
    std::vector<std::string> hello = {"HELLO", std::string(version), featureList, std::to_string(pid)};
    std::string framed = frame(joinFields(hello));
    if (!sendAll(framed)) {
        if (logger_) logger_->warn("control channel: HELLO send failed; not serving");
        return;
    }

    running_.store(true);
    worker_ = std::thread([this] { serve(); });
}

void ControlChannel::serve() {
    const SocketHandle fd = fd_.load(std::memory_order_acquire);
    std::string buffer;
    char chunk[4096];
    while (running_.load()) {
        long long n = recvSome(fd, chunk, sizeof chunk);
        if (n <= 0) {
            break; // peer closed or error
        }
        buffer.append(chunk, static_cast<std::size_t>(n));

        while (std::optional<std::string> payload = tryReadFrame(buffer)) {
            std::vector<std::string_view> fields = splitFields(*payload);
            if (fields.empty() || fields[0] != "REQ" || fields.size() < 3) {
                continue;
            }
            std::string_view id = fields[1];
            std::string_view cmd = fields[2];
            std::span<const std::string_view> args(fields.data() + 3, fields.size() - 3);

            Reply reply;
            try {
                reply = handler_
                    ? handler_(cmd, args)
                    : Reply::error("no handler");
            } catch (const std::exception& ex) {
                if (logger_) logger_->error("control handler failed: {}", ex.what());
                reply = Reply::error(ex.what());
            } catch (...) {
                if (logger_) logger_->error("control handler failed");
                reply = Reply::error("internal profiler error");
            }
            std::vector<std::string> res = {"RES", std::string(id), reply.ok ? "ok" : "err", reply.detail};
            std::string framed = frame(joinFields(res));
            sendAll(framed);
        }
    }
    if (running_.exchange(false) && disconnected_) {
        try {
            disconnected_();
        } catch (const std::exception& ex) {
            if (logger_) logger_->error("control channel disconnect handler failed: {}", ex.what());
        } catch (...) {
            if (logger_) logger_->error("control channel disconnect handler failed");
        }
    }
}

bool ControlChannel::sendEvent(const std::vector<std::string>& fields) {
    std::vector<std::string> all;
    all.reserve(fields.size() + 1);
    all.emplace_back("EVENT");
    for (const std::string& f : fields) {
        all.push_back(f);
    }
    std::string framed = frame(joinFields(all));
    return sendAll(framed);
}

bool ControlChannel::sendAll(std::span<const char> bytes) {
    std::lock_guard<std::mutex> lock(writeMutex_); // also keeps stop()'s close() from racing this send
    const SocketHandle fd = fd_.load(std::memory_order_acquire);
    if (fd == kInvalidSocket) {
        return false;
    }
    std::size_t sent = 0;
    while (sent < bytes.size()) {
        long long n = sendSome(fd, bytes.data() + sent, bytes.size() - sent);
        if (n <= 0) {
            return false;
        }
        sent += static_cast<std::size_t>(n);
    }
    return true;
}

void ControlChannel::stop() {
    running_.store(false);
    const SocketHandle fd = fd_.load(std::memory_order_acquire);
    if (fd != kInvalidSocket) {
        shutdownSocket(fd); // unblock a blocked recv()/send() in serve()/sendAll(); safe to race, the fd stays valid until close()
    }
    if (worker_.joinable()) {
        worker_.join(); // serve() only touches fd_ up to this point
    }

    // Take writeMutex_ before closing so an in-flight sendAll() (e.g. an EVENT pushed from a GC
    // callback thread) always completes against a still-open fd rather than a reused one.
    std::lock_guard<std::mutex> lock(writeMutex_);
    const SocketHandle toClose = fd_.exchange(kInvalidSocket, std::memory_order_acq_rel);
    if (toClose != kInvalidSocket) {
        closeSocket(toClose);
    }
#ifdef _WIN32
    if (wsaOwned_) {
        wsaRelease();
        wsaOwned_ = false;
    }
#endif
}

} // namespace Sherlock::control
