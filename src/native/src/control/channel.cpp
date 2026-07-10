#include "sherlock/control/channel.hpp"

#include "sherlock/common/logger.hpp"
#include "sherlock/control/protocol.hpp"

#include <utility>

#ifndef _WIN32
#include <cerrno>
#include <cstring>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>
#else
#include <process.h> // _getpid
#endif

namespace Sherlock::control {

namespace {
#ifdef MSG_NOSIGNAL
constexpr int kSendFlags = MSG_NOSIGNAL; // Linux: don't raise SIGPIPE on a closed peer
#else
constexpr int kSendFlags = 0;            // macOS uses SO_NOSIGPIPE (set below) instead
#endif
} // namespace

ControlChannel::ControlChannel(Logger* logger) : logger_(logger) {}

ControlChannel::~ControlChannel() {
    stop();
}

std::optional<std::string> ControlChannel::connect(const std::string& socketPath) {
#ifdef _WIN32
    (void)socketPath;
    return std::string("control channel not implemented on Windows yet");
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

    fd_ = fd;
    return std::nullopt;
#endif
}

void ControlChannel::start(std::string_view version, const std::vector<std::string>& features, Handler handler) {
    if (fd_ < 0) {
        return;
    }
    handler_ = std::move(handler);

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
    sendAll(framed);

    running_.store(true);
    worker_ = std::thread([this] { serve(); });
}

void ControlChannel::serve() {
#ifndef _WIN32
    std::string buffer;
    char chunk[4096];
    while (running_.load()) {
        ssize_t n = ::recv(fd_, chunk, sizeof chunk, 0);
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

            Reply reply = handler_ ? handler_(cmd, args) : Reply::error("no handler");
            std::vector<std::string> res = {"RES", std::string(id), reply.ok ? "ok" : "err", reply.detail};
            std::string framed = frame(joinFields(res));
            sendAll(framed);
        }
    }
    if (logger_) {
        logger_->logDebug("control channel disconnected");
    }
#endif
}

void ControlChannel::sendEvent(const std::vector<std::string>& fields) {
    std::vector<std::string> all;
    all.reserve(fields.size() + 1);
    all.emplace_back("EVENT");
    for (const std::string& f : fields) {
        all.push_back(f);
    }
    std::string framed = frame(joinFields(all));
    sendAll(framed);
}

bool ControlChannel::sendAll(std::span<const char> bytes) {
#ifdef _WIN32
    (void)bytes;
    return false;
#else
    std::lock_guard<std::mutex> lock(writeMutex_);
    std::size_t sent = 0;
    while (sent < bytes.size()) {
        ssize_t n = ::send(fd_, bytes.data() + sent, bytes.size() - sent, kSendFlags);
        if (n <= 0) {
            return false;
        }
        sent += static_cast<std::size_t>(n);
    }
    return true;
#endif
}

void ControlChannel::stop() {
    running_.store(false);
#ifndef _WIN32
    if (fd_ >= 0) {
        ::shutdown(fd_, SHUT_RDWR); // unblock recv() in serve()
    }
#endif
    if (worker_.joinable()) {
        worker_.join();
    }
#ifndef _WIN32
    if (fd_ >= 0) {
        ::close(fd_);
        fd_ = -1;
    }
#endif
}

} // namespace Sherlock::control
