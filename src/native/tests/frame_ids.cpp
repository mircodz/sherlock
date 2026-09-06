#include "sherlock/profiler/shadowstack.hpp"

#include <gtest/gtest.h>

#include <cstdint>
#include <utility>

using namespace Sherlock;

namespace {
struct StackScope {
    shadow::ThreadStack* previous = std::exchange(shadow::t_stack, nullptr);
    ~StackScope() {
        delete shadow::t_stack;
        shadow::t_stack = previous;
    }
};
} // namespace

TEST(ShadowFrameIds, KeepsFullWidthCookiesInCallOrder) {
    StackScope stack;
    constexpr FrameId outer = (FrameId{1} << 40) + 17;
    constexpr FrameId inner = (FrameId{1} << 48) + 31;

    Sherlock_ShadowPush(static_cast<std::int64_t>(outer));
    Sherlock_ShadowPush(static_cast<std::int64_t>(inner));

    ASSERT_EQ(shadow::storedDepth(), 2);
    EXPECT_EQ(shadow::frames()[0], outer);
    EXPECT_EQ(shadow::frames()[1], inner);
    Sherlock_ShadowPop();
    EXPECT_EQ(shadow::storedDepth(), 1);
    EXPECT_EQ(shadow::frames()[0], outer);
    Sherlock_ShadowPop();
    EXPECT_EQ(shadow::storedDepth(), 0);
}

TEST(ShadowFrameIds, DepthOverflowStillBalancesWithoutChangingStoredFrames) {
    StackScope stack;
    for (std::size_t i = 0; i < shadow::kMaxShadow + 5; ++i) {
        Sherlock_ShadowPush(static_cast<std::int64_t>(i + 1));
    }

    ASSERT_EQ(shadow::storedDepth(), shadow::kMaxShadow);
    EXPECT_EQ(shadow::frames()[shadow::kMaxShadow - 1], shadow::kMaxShadow);
    for (unsigned i = 0; i < 5; ++i) {
        Sherlock_ShadowPop();
    }
    EXPECT_EQ(shadow::storedDepth(), shadow::kMaxShadow);
    for (std::size_t i = 0; i < shadow::kMaxShadow; ++i) {
        Sherlock_ShadowPop();
    }
    Sherlock_ShadowPop();
    EXPECT_EQ(shadow::storedDepth(), 0);
}
