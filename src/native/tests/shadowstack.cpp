// Every thread that runs instrumented code gets a shadow stack; it must not outlive the thread.

#include "sherlock/profiler/shadowstack.hpp"

#include <gtest/gtest.h>

#include <thread>
#include <vector>

using namespace Sherlock;

TEST(ShadowStack, AThreadsStackIsFreedWhenItExits) {
    const std::size_t before = shadow::liveStacks();
    std::vector<std::thread> threads;
    for (int i = 0; i < 64; ++i) {
        threads.emplace_back([] {
            for (int depth = 0; depth < 4096; ++depth) {
                Sherlock_ShadowPush(depth); // grows the stack past its initial size
            }
            EXPECT_EQ(shadow::storedDepth(), 4096u);
            for (int depth = 0; depth < 4096; ++depth) {
                Sherlock_ShadowPop();
            }
        });
    }
    for (std::thread& thread : threads) {
        thread.join();
    }
    EXPECT_EQ(shadow::liveStacks(), before);
}

// TryPush runs without a GC transition, so it leaves allocating and growing a thread's stack to Push.
TEST(ShadowStack, TryPushOnlyFillsRoomPushHasMade) {
    std::thread([] {
        EXPECT_EQ(Sherlock_ShadowTryPush(1), 0);
        EXPECT_EQ(shadow::t_stack, nullptr);

        Sherlock_ShadowPush(1);
        for (std::int64_t frame = 2; frame <= static_cast<std::int64_t>(shadow::kInitialShadow); ++frame) {
            EXPECT_EQ(Sherlock_ShadowTryPush(frame), 1);
        }
        EXPECT_EQ(Sherlock_ShadowTryPush(0), 0);
        Sherlock_ShadowPush(static_cast<std::int64_t>(shadow::kInitialShadow) + 1);
        EXPECT_EQ(Sherlock_ShadowTryPush(static_cast<std::int64_t>(shadow::kInitialShadow) + 2), 1);

        ASSERT_EQ(shadow::storedDepth(), shadow::kInitialShadow + 2);
        for (std::uint32_t depth = 0; depth < shadow::storedDepth(); ++depth) {
            EXPECT_EQ(shadow::frames()[depth], depth + 1);
        }
    }).join();
}

// Past what a stack can store, TryPush counts frames as Push does, so one pop still undoes it.
TEST(ShadowStack, TryPushCountsFramesAStackCannotStore) {
    for (bool atLimit : {false, true}) {
        std::thread([atLimit] {
            Sherlock_ShadowPush(0);
            shadow::t_stack->growable = atLimit; // false as after a failed growth
            while (shadow::t_stack->depth < (atLimit ? shadow::kMaxShadow : shadow::kInitialShadow)) {
                Sherlock_ShadowPush(0);
            }
            const std::uint32_t stored = shadow::storedDepth();
            EXPECT_EQ(Sherlock_ShadowTryPush(1), 1);
            EXPECT_EQ(shadow::storedDepth(), stored);
            EXPECT_EQ(shadow::t_stack->depth, stored + 1);
            Sherlock_ShadowPop();
            EXPECT_EQ(shadow::t_stack->depth, stored);
        }).join();
    }
}
