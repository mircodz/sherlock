#include "sherlock/control/protocol.hpp"

#include <gtest/gtest.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <string>
#include <thread>

using namespace Sherlock::control;
using namespace std::chrono_literals;

TEST(CoherentCaptureBarrier, StartsIdle) {
    CoherentCaptureBarrier barrier;
    EXPECT_EQ(barrier.state(), CoherentCaptureBarrier::State::Idle);
    EXPECT_FALSE(barrier.active());
    EXPECT_EQ(barrier.token(), "");
}

TEST(CoherentCaptureBarrier, BeginArmsAndOnlyOneBarrierAtATime) {
    CoherentCaptureBarrier barrier;
    EXPECT_TRUE(barrier.begin("tok-1"));
    EXPECT_EQ(barrier.state(), CoherentCaptureBarrier::State::Arming);
    EXPECT_TRUE(barrier.active());
    EXPECT_EQ(barrier.token(), "tok-1");

    // A second begin() while one is already in flight must fail without disturbing the first.
    EXPECT_FALSE(barrier.begin("tok-2"));
    EXPECT_EQ(barrier.state(), CoherentCaptureBarrier::State::Arming);
    EXPECT_EQ(barrier.token(), "tok-1");
}

TEST(CoherentCaptureBarrier, MarkReadyRejectsAnythingOtherThanArming) {
    CoherentCaptureBarrier barrier;
    // Idle: no capture armed, so an unrelated GC finishing must not transition anything.
    EXPECT_FALSE(barrier.markReady(7));
    EXPECT_EQ(barrier.state(), CoherentCaptureBarrier::State::Idle);

    ASSERT_TRUE(barrier.begin("tok"));
    EXPECT_TRUE(barrier.markReady(7));
    EXPECT_EQ(barrier.state(), CoherentCaptureBarrier::State::Parked);

    // A second GC finishing while already Parked (this GC's callback hasn't parked yet in this unit
    // test - there's no real GC thread here) must not be treated as another armed capture.
    EXPECT_FALSE(barrier.markReady(8));
}

TEST(CoherentCaptureBarrier, AbortResetsAnArmingBarrierWithoutAGc) {
    CoherentCaptureBarrier barrier;
    ASSERT_TRUE(barrier.begin("tok"));

    // Wrong token: must not touch the real one.
    EXPECT_FALSE(barrier.abort("other-token"));
    EXPECT_EQ(barrier.state(), CoherentCaptureBarrier::State::Arming);

    EXPECT_TRUE(barrier.abort("tok"));
    EXPECT_EQ(barrier.state(), CoherentCaptureBarrier::State::Idle);
    EXPECT_FALSE(barrier.active());
    EXPECT_EQ(barrier.token(), "");

    // Idle again: a fresh begin() must succeed.
    EXPECT_TRUE(barrier.begin("tok-2"));
}

TEST(CoherentCaptureBarrier, AbortWakesAParkedBarrier) {
    CoherentCaptureBarrier barrier;
    ASSERT_TRUE(barrier.begin("tok"));
    ASSERT_TRUE(barrier.markReady(3));

    std::thread parked([&] {
        EXPECT_EQ(barrier.park(10s), CoherentCaptureBarrier::ParkResult::Released);
    });

    EXPECT_TRUE(barrier.abort("tok"));
    parked.join();
    EXPECT_EQ(barrier.state(), CoherentCaptureBarrier::State::Idle);
}

TEST(CoherentCaptureBarrier, IsParkedForOnlyMatchesTheArmedToken) {
    CoherentCaptureBarrier barrier;
    ASSERT_TRUE(barrier.begin("tok"));
    EXPECT_FALSE(barrier.isParkedFor("tok")); // still Arming, not Parked yet
    ASSERT_TRUE(barrier.markReady(3));
    EXPECT_TRUE(barrier.isParkedFor("tok"));
    EXPECT_FALSE(barrier.isParkedFor("wrong-token"));
}

TEST(CoherentCaptureBarrier, ReleaseValidatesTokenAndReportsTheRecordedGcCount) {
    CoherentCaptureBarrier barrier;
    ASSERT_TRUE(barrier.begin("tok"));
    ASSERT_TRUE(barrier.markReady(42));

    std::uint64_t gcCount = 0;
    EXPECT_FALSE(barrier.release("wrong-token", gcCount)); // must not release on a token mismatch
    EXPECT_EQ(barrier.state(), CoherentCaptureBarrier::State::Parked);

    EXPECT_TRUE(barrier.release("tok", gcCount));
    EXPECT_EQ(gcCount, 42u);

    // A second release() for the same (now-consumed) token must fail: nothing is parked anymore.
    EXPECT_FALSE(barrier.release("tok", gcCount));
}

TEST(CoherentCaptureBarrier, ReleaseFailsWhenNothingIsParked) {
    CoherentCaptureBarrier barrier;
    std::uint64_t gcCount = 0;
    EXPECT_FALSE(barrier.release("tok", gcCount)); // Idle
    ASSERT_TRUE(barrier.begin("tok"));
    EXPECT_FALSE(barrier.release("tok", gcCount)); // Arming, not yet Parked
}

TEST(CoherentCaptureBarrier, ForceReleaseIsANoOpWhenIdle) {
    CoherentCaptureBarrier barrier;
    barrier.forceRelease();
    EXPECT_EQ(barrier.state(), CoherentCaptureBarrier::State::Idle);
}

// --- Real concurrency: a "parked GC callback" thread and a "control thread" rendezvousing. ---

TEST(CoherentCaptureBarrier, ParkBlocksUntilReleaseWakesIt) {
    CoherentCaptureBarrier barrier;
    ASSERT_TRUE(barrier.begin("tok"));
    ASSERT_TRUE(barrier.markReady(5));

    std::atomic<bool> parkReturned{false};
    std::thread parked([&] {
        auto result = barrier.park(10s); // long relative to the release below; must not time out
        EXPECT_EQ(result, CoherentCaptureBarrier::ParkResult::Released);
        parkReturned.store(true);
    });

    // Give the parked thread a moment to actually enter the wait before releasing it.
    std::this_thread::sleep_for(20ms);
    EXPECT_FALSE(parkReturned.load());

    std::uint64_t gcCount = 0;
    EXPECT_TRUE(barrier.release("tok", gcCount));
    EXPECT_EQ(gcCount, 5u);
    parked.join();
    EXPECT_TRUE(parkReturned.load());
    EXPECT_EQ(barrier.state(), CoherentCaptureBarrier::State::Idle); // park() always resets to Idle
}

TEST(CoherentCaptureBarrier, ParkTimesOutAndAlwaysReleases) {
    CoherentCaptureBarrier barrier;
    ASSERT_TRUE(barrier.begin("tok"));
    ASSERT_TRUE(barrier.markReady(9));

    auto start = std::chrono::steady_clock::now();
    auto result = barrier.park(30ms); // nobody ever calls release() - must time out, not hang
    auto elapsed = std::chrono::steady_clock::now() - start;

    EXPECT_EQ(result, CoherentCaptureBarrier::ParkResult::TimedOut);
    EXPECT_GE(elapsed, 30ms);
    EXPECT_EQ(barrier.state(), CoherentCaptureBarrier::State::Idle); // a timeout still releases fully
    EXPECT_FALSE(barrier.active());
}

TEST(CoherentCaptureBarrier, ForceReleaseWakesAParkedWaiterLikeShutdown) {
    CoherentCaptureBarrier barrier;
    ASSERT_TRUE(barrier.begin("tok"));
    ASSERT_TRUE(barrier.markReady(1));

    std::thread parked([&] {
        auto result = barrier.park(10s);
        EXPECT_EQ(result, CoherentCaptureBarrier::ParkResult::Released);
    });

    std::this_thread::sleep_for(20ms);
    barrier.forceRelease(); // simulates Shutdown() releasing a parked capture, no token needed
    parked.join();
    EXPECT_EQ(barrier.state(), CoherentCaptureBarrier::State::Idle);
}

TEST(CoherentCaptureBarrier, ForceReleaseDuringArmingIsPickedUpByTheUpcomingPark) {
    // Regression: forceRelease() arriving before the armed GC has even reached markReady()/park()
    // (e.g. Shutdown racing a slow ForceGC) must not be lost - the subsequent park() must wake
    // immediately instead of waiting out its full timeout.
    CoherentCaptureBarrier barrier;
    ASSERT_TRUE(barrier.begin("tok"));
    barrier.forceRelease(); // still Arming: no one is parked yet

    ASSERT_TRUE(barrier.markReady(2)); // the GC eventually "happens" and reaches the callback

    auto start = std::chrono::steady_clock::now();
    auto result = barrier.park(10s); // must return immediately, not block for 10s
    auto elapsed = std::chrono::steady_clock::now() - start;

    EXPECT_EQ(result, CoherentCaptureBarrier::ParkResult::Released);
    EXPECT_LT(elapsed, 2s);
}

TEST(CoherentCaptureBarrier, CompleteAbortRaceOnlyOneReleaseWins) {
    // Simulates a complete/abort race for the same parked token: only one of two concurrent
    // release() calls may succeed.
    CoherentCaptureBarrier barrier;
    ASSERT_TRUE(barrier.begin("tok"));
    ASSERT_TRUE(barrier.markReady(11));

    std::atomic<int> successes{0};
    auto tryRelease = [&] {
        std::uint64_t gcCount = 0;
        if (barrier.release("tok", gcCount)) {
            successes.fetch_add(1);
        }
    };
    std::thread a(tryRelease);
    std::thread b(tryRelease);
    a.join();
    b.join();

    EXPECT_EQ(successes.load(), 1);
}
