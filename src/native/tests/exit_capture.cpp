#include "sherlock/control/protocol.hpp"

#include <gtest/gtest.h>

#include <atomic>
#include <chrono>
#include <thread>

using namespace Sherlock::control;
using namespace std::chrono_literals;

TEST(ExitCaptureLatch, ReleaseValidatesTokenAndWakesReturn) {
    ExitCaptureLatch latch;
    ASSERT_TRUE(latch.begin("exit-1"));
    EXPECT_FALSE(latch.release("stale"));

    std::atomic<bool> returned{false};
    std::thread waiting([&] {
        EXPECT_EQ(latch.wait(10s), ExitCaptureLatch::WaitResult::Released);
        returned.store(true);
    });

    EXPECT_TRUE(latch.release("exit-1"));
    waiting.join();
    EXPECT_TRUE(returned.load());
    EXPECT_FALSE(latch.active());
}

TEST(ExitCaptureLatch, ReleaseBeforeWaitIsPreserved) {
    ExitCaptureLatch latch;
    ASSERT_TRUE(latch.begin("exit-1"));
    ASSERT_TRUE(latch.release("exit-1"));

    EXPECT_EQ(latch.wait(10s), ExitCaptureLatch::WaitResult::Released);
    EXPECT_FALSE(latch.active());
}

TEST(ExitCaptureLatch, OnlyOneReleaseWins) {
    ExitCaptureLatch latch;
    ASSERT_TRUE(latch.begin("exit-1"));
    EXPECT_TRUE(latch.release("exit-1"));
    EXPECT_FALSE(latch.release("exit-1"));
}

TEST(ExitCaptureLatch, ForceReleaseWakesReturn) {
    ExitCaptureLatch latch;
    ASSERT_TRUE(latch.begin("exit-1"));
    latch.forceRelease();

    EXPECT_EQ(latch.wait(10s), ExitCaptureLatch::WaitResult::Released);
}

TEST(ExitCaptureLatch, TimeoutResetsLatch) {
    ExitCaptureLatch latch;
    ASSERT_TRUE(latch.begin("exit-1"));

    EXPECT_EQ(latch.wait(10ms), ExitCaptureLatch::WaitResult::TimedOut);
    EXPECT_FALSE(latch.active());
    EXPECT_TRUE(latch.begin("exit-2"));
}
