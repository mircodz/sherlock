// Coverage for the sl <-> profiler control channel (protocol.hpp framing plus the
// ControlChannel transport in channel.cpp).
//
// The framing/field tests run on every platform: they exercise the exact wire format
// (frame/tryReadFrame/splitFields/joinFields) that both HELLO/REQ/RES/EVENT messages use.
//
// The transport round-trip test drives a real ControlChannel against a POSIX AF_UNIX
// listening socket standing in for sl, and only builds on Unix (`#ifndef _WIN32`) since it
// uses raw socket()/bind()/accept() to play the server role. ControlChannel::connect/serve/
// sendAll/stop on Windows use Winsock's AF_UNIX support (WSAStartup, afunix.h's sockaddr_un,
// send/recv/shutdown/closesocket) instead of the POSIX calls exercised here; that path -
// including WSAStartup/WSACleanup refcounting and Winsock error formatting - still needs a
// manual run on a Windows runner (CI already builds+ctests this target for win-x64).

#include "sherlock/control/channel.hpp"

#include "sherlock/control/protocol.hpp"

#include <gtest/gtest.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

using namespace Sherlock::control;

TEST(ControlProtocol, FramesAndParsesAPayloadRoundTrip) {
    std::string framed = frame("REQ\t1\tping");
    ASSERT_EQ(framed.size(), 4u + 10u);

    std::string buffer = framed;
    std::optional<std::string> payload = tryReadFrame(buffer);
    ASSERT_TRUE(payload.has_value());
    EXPECT_EQ(*payload, "REQ\t1\tping");
    EXPECT_TRUE(buffer.empty()); // the frame was consumed
}

TEST(ControlProtocol, TryReadFrameWaitsForACompleteFrame) {
    std::string framed = frame("EVENT\tsnapshot-trigger");
    std::string buffer = framed.substr(0, framed.size() - 1); // withhold the last byte
    EXPECT_FALSE(tryReadFrame(buffer).has_value());
    EXPECT_EQ(buffer.size(), framed.size() - 1); // left intact, not partially consumed

    buffer.push_back(framed.back());
    std::optional<std::string> payload = tryReadFrame(buffer);
    ASSERT_TRUE(payload.has_value());
    EXPECT_EQ(*payload, "EVENT\tsnapshot-trigger");
}

TEST(ControlProtocol, ReadsBackToBackFramesInOneBuffer) {
    std::string buffer = frame("HELLO\t1.0\tfeat-a,feat-b\t4242") + frame("RES\t1\tok\tpong");

    std::optional<std::string> first = tryReadFrame(buffer);
    ASSERT_TRUE(first.has_value());
    EXPECT_EQ(*first, "HELLO\t1.0\tfeat-a,feat-b\t4242");

    std::optional<std::string> second = tryReadFrame(buffer);
    ASSERT_TRUE(second.has_value());
    EXPECT_EQ(*second, "RES\t1\tok\tpong");

    EXPECT_FALSE(tryReadFrame(buffer).has_value());
}

TEST(ControlProtocol, SplitAndJoinFieldsRoundTrip) {
    std::vector<std::string_view> fields = splitFields("REQ\t7\temit-correlation\targ1\targ2");
    ASSERT_EQ(fields.size(), 5u);
    EXPECT_EQ(fields[0], "REQ");
    EXPECT_EQ(fields[1], "7");
    EXPECT_EQ(fields[2], "emit-correlation");
    EXPECT_EQ(fields[3], "arg1");
    EXPECT_EQ(fields[4], "arg2");
    EXPECT_EQ(joinFields(fields), "REQ\t7\temit-correlation\targ1\targ2");
}

TEST(ControlProtocol, SplitFieldsAlwaysReturnsAtLeastOneField) {
    std::vector<std::string_view> fields = splitFields("");
    ASSERT_EQ(fields.size(), 1u);
    EXPECT_EQ(fields[0], "");
}

#ifndef _WIN32

#include <cerrno>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

namespace {

// A minimal blocking AF_UNIX server standing in for sl: accepts one connection and lets the
// test read/write framed messages on it directly.
class FakeServer {
public:
    explicit FakeServer(const std::filesystem::path& path) : path_(path) {
        ::unlink(path.c_str());
        listenFd_ = ::socket(AF_UNIX, SOCK_STREAM, 0);
        sockaddr_un addr{};
        addr.sun_family = AF_UNIX;
        std::strncpy(addr.sun_path, path.c_str(), sizeof(addr.sun_path) - 1);
        if (::bind(listenFd_, reinterpret_cast<sockaddr*>(&addr), sizeof addr) != 0 ||
            ::listen(listenFd_, 1) != 0) {
            ADD_FAILURE() << "failed to set up fake server: " << std::strerror(errno);
        }
    }

    ~FakeServer() {
        if (clientFd_ >= 0) ::close(clientFd_);
        if (listenFd_ >= 0) ::close(listenFd_);
        ::unlink(path_.c_str());
    }

    void accept() { clientFd_ = ::accept(listenFd_, nullptr, nullptr); }

    void disconnect() {
        ::shutdown(clientFd_, SHUT_RDWR);
        ::close(clientFd_);
        clientFd_ = -1;
    }

    // Reads exactly one framed payload (blocking).
    std::string readFrame() {
        std::string buffer;
        char chunk[4096];
        for (;;) {
            if (std::optional<std::string> payload = tryReadFrame(buffer)) {
                return *payload;
            }
            ssize_t n = ::recv(clientFd_, chunk, sizeof chunk, 0);
            if (n <= 0) {
                return {};
            }
            buffer.append(chunk, static_cast<std::size_t>(n));
        }
    }

    void writeFrame(const std::string& payload) {
        std::string framed = frame(payload);
        std::size_t sent = 0;
        while (sent < framed.size()) {
            ssize_t n = ::send(clientFd_, framed.data() + sent, framed.size() - sent, 0);
            ASSERT_GT(n, 0);
            sent += static_cast<std::size_t>(n);
        }
    }

    // Returns true once recv() observes an orderly close (ControlChannel::stop() shutting down).
    bool waitForClose(std::chrono::milliseconds timeout) {
        auto deadline = std::chrono::steady_clock::now() + timeout;
        char c;
        while (std::chrono::steady_clock::now() < deadline) {
            ssize_t n = ::recv(clientFd_, &c, 1, MSG_DONTWAIT);
            if (n == 0) return true;
            if (n < 0 && errno != EWOULDBLOCK && errno != EAGAIN) return false;
            std::this_thread::sleep_for(std::chrono::milliseconds(5));
        }
        return false;
    }

private:
    std::filesystem::path path_;
    int listenFd_ = -1;
    int clientFd_ = -1;
};

// sockaddr_un::sun_path is a short fixed buffer (104 bytes on macOS, 108 on Linux); a path under
// std::filesystem::temp_directory_path() can easily overflow that in a CI sandbox with a long
// TMPDIR, so use a dedicated short-named directory under /tmp instead.
std::filesystem::path testSocketDir() {
    static std::filesystem::path dir = [] {
        char tmpl[] = "/tmp/sl-ctlXXXXXX";
        char* made = ::mkdtemp(tmpl);
        if (!made) {
            ADD_FAILURE() << "mkdtemp failed: " << std::strerror(errno);
            return std::filesystem::path("/tmp");
        }
        return std::filesystem::path(made);
    }();
    return dir;
}

std::filesystem::path tempSocketPath(std::string_view name) {
    static std::atomic<std::uint64_t> sequence{1};
    return testSocketDir() /
           (std::string(name) + "-" + std::to_string(sequence.fetch_add(1, std::memory_order_relaxed)) + ".sock");
}

} // namespace

TEST(ControlChannel, ConnectsSendsHelloAndServesRequestsAndEvents) {
    std::filesystem::path path = tempSocketPath("roundtrip");
    FakeServer server(path);

    ControlChannel channel(nullptr);
    std::optional<std::string> err = channel.connect(path.string());
    ASSERT_FALSE(err.has_value()) << err.value_or("");

    server.accept();

    std::atomic<int> handlerCalls{0};
    channel.start("1.2.3", {"probes", "triggers"}, [&](std::string_view cmd, std::span<const std::string_view> args) {
        ++handlerCalls;
        EXPECT_EQ(cmd, "ping");
        EXPECT_TRUE(args.empty());
        return Reply::success("pong");
    });

    // HELLO: verb, version, comma-joined features, pid.
    std::string helloPayload = server.readFrame();
    std::vector<std::string_view> hello = splitFields(helloPayload);
    ASSERT_EQ(hello.size(), 4u);
    EXPECT_EQ(hello[0], "HELLO");
    EXPECT_EQ(hello[1], "1.2.3");
    EXPECT_EQ(hello[2], "probes,triggers");
    EXPECT_FALSE(hello[3].empty()); // pid

    EXPECT_TRUE(channel.connected());

    // REQ/RES round trip.
    server.writeFrame("REQ\t1\tping");
    std::string resPayload = server.readFrame();
    std::vector<std::string_view> res = splitFields(resPayload);
    ASSERT_EQ(res.size(), 4u);
    EXPECT_EQ(res[0], "RES");
    EXPECT_EQ(res[1], "1");
    EXPECT_EQ(res[2], "ok");
    EXPECT_EQ(res[3], "pong");
    EXPECT_EQ(handlerCalls.load(), 1);

    // Unsolicited EVENT frame.
    EXPECT_TRUE(channel.sendEvent({"snapshot-trigger", "GC#3"}));
    std::string eventPayload = server.readFrame();
    std::vector<std::string_view> event = splitFields(eventPayload);
    ASSERT_EQ(event.size(), 3u);
    EXPECT_EQ(event[0], "EVENT");
    EXPECT_EQ(event[1], "snapshot-trigger");
    EXPECT_EQ(event[2], "GC#3");

    channel.stop();
    EXPECT_FALSE(channel.connected());
    EXPECT_TRUE(server.waitForClose(std::chrono::milliseconds(2000)));
}

TEST(ControlChannel, ConnectFailsWithAnActionableMessageWhenNothingIsListening) {
    std::filesystem::path path = tempSocketPath("no-listener");
    std::filesystem::remove(path); // ensure nothing is bound here

    ControlChannel channel(nullptr);
    std::optional<std::string> err = channel.connect(path.string());
    ASSERT_TRUE(err.has_value());
    EXPECT_FALSE(err->empty());
    EXPECT_FALSE(channel.connected());
}

TEST(ControlChannel, ReportsPeerDisconnect) {
    std::filesystem::path path = tempSocketPath("disconnect");
    FakeServer server(path);

    ControlChannel channel(nullptr);
    ASSERT_FALSE(channel.connect(path.string()).has_value());
    server.accept();

    std::atomic<bool> disconnected{false};
    channel.start("1.2.3", {}, [](std::string_view, std::span<const std::string_view>) {
        return Reply::success();
    }, [&] {
        disconnected.store(true);
    });
    (void)server.readFrame();
    server.disconnect();

    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
    while (!disconnected.load() && std::chrono::steady_clock::now() < deadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    EXPECT_TRUE(disconnected.load());
    channel.stop();
}

#endif // !_WIN32
