// Snapshot triggers are armed on the control thread while CLR callbacks match them on others.

#include "sherlock/profiler/triggers.hpp"

#include <gtest/gtest.h>

#include <atomic>
#include <set>
#include <string>
#include <thread>
#include <vector>

using namespace Sherlock;
using Kind = SnapshotTriggers::Kind;

TEST(SnapshotTriggers, AFiredTriggerStopsAllocationMatching) {
    SnapshotTriggers triggers;
    EXPECT_FALSE(triggers.wantsAlloc());
    triggers.add(Kind::Alloc, "App.Order", "alloc:App.Order");
    EXPECT_TRUE(triggers.wantsAlloc());

    EXPECT_EQ(triggers.onAlloc("App.Customer"), std::nullopt);
    EXPECT_EQ(triggers.onAlloc("App.Order"), "alloc:App.Order");
    // One-shot: allocations no longer need their type resolved.
    EXPECT_FALSE(triggers.wantsAlloc());
    EXPECT_EQ(triggers.onAlloc("App.Order"), std::nullopt);
}

TEST(SnapshotTriggers, KindsAreCountedApart) {
    SnapshotTriggers triggers;
    triggers.add(Kind::Throw, "", "throw");
    triggers.add(Kind::Gc, "gen2", "gc:gen2");
    EXPECT_FALSE(triggers.wantsAlloc());

    EXPECT_EQ(triggers.onGc(1), std::nullopt);
    EXPECT_EQ(triggers.onGc(2), "gc:gen2");
    EXPECT_FALSE(triggers.wantsGc());
    EXPECT_TRUE(triggers.wantsThrow());
    EXPECT_EQ(triggers.onThrow("System.InvalidOperationException"), "throw");
    EXPECT_FALSE(triggers.wantsThrow());
}

TEST(SnapshotTriggers, ArmingWhileCallbacksMatchFiresEachTriggerOnce) {
    constexpr int kTriggers = 300;
    constexpr int kMatchers = 4;
    SnapshotTriggers triggers;
    std::atomic<bool> armed{false};
    std::vector<std::vector<std::string>> fired(kMatchers);

    std::thread control([&] {
        for (int i = 0; i < kTriggers; ++i) {
            triggers.add(Kind::Alloc, "T" + std::to_string(i), "alloc:T" + std::to_string(i));
        }
        armed = true;
    });
    std::vector<std::thread> matchers;
    for (int m = 0; m < kMatchers; ++m) {
        matchers.emplace_back([&, m] {
            for (int round = 0; !armed || triggers.wantsAlloc(); ++round) {
                if (auto display = triggers.onAlloc("T" + std::to_string((round * 7 + m) % kTriggers))) {
                    fired[m].push_back(*display);
                }
                (void)triggers.onGc(2);
            }
        });
    }
    control.join();
    for (std::thread& matcher : matchers) {
        matcher.join();
    }

    std::multiset<std::string> all;
    for (const std::vector<std::string>& displays : fired) {
        all.insert(displays.begin(), displays.end());
    }
    EXPECT_EQ(all.size(), static_cast<std::size_t>(kTriggers));
    EXPECT_EQ(std::set<std::string>(all.begin(), all.end()).size(), all.size()); // none fired twice
    EXPECT_FALSE(triggers.wantsAlloc());
}
