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
