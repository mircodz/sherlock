#include "sherlock/profiler/probe.hpp"

#include <gtest/gtest.h>

#include <atomic>
#include <string>
#include <thread>
#include <utility>
#include <vector>

using namespace Sherlock;

namespace {

constexpr ModuleID kModule = static_cast<ModuleID>(0x1000);
constexpr mdMethodDef kMethod = static_cast<mdMethodDef>(0x06000001);

} // namespace

TEST(ProbeRegistry, DispatchesOnlyTheEmbeddedEnterHookOnce) {
    ProbeRegistry registry;
    std::vector<std::pair<std::string, ProbePhase>> hits;
    registry.setHitCallback([&](const std::string& display, ProbePhase phase) {
        hits.emplace_back(display, phase);
    });

    ProbeRegistry::Registration registration =
        registry.registerMethod(kModule, kMethod, "App.Query", ProbeEvents::Enter);

    ASSERT_TRUE(registration.inserted);
    ASSERT_TRUE(registration.changed);
    ASSERT_TRUE(registration.plan);
    EXPECT_TRUE(registration.plan.onEnter());
    EXPECT_FALSE(registration.plan.onExit());
    EXPECT_EQ(registry.planFor(kModule, kMethod).cookie, registration.plan.cookie);

    ProbeRegistry::dispatch(registration.plan.cookie, ProbePhase::Enter);
    ProbeRegistry::dispatch(registration.plan.cookie, ProbePhase::Enter);
    ProbeRegistry::dispatch(registration.plan.cookie, ProbePhase::Exit);

    ASSERT_EQ(hits.size(), 1u);
    EXPECT_EQ(hits[0].first, "App.Query");
    EXPECT_EQ(hits[0].second, ProbePhase::Enter);
}

TEST(ProbeRegistry, CanComposeEnterAndExitWithoutChangingTheCookie) {
    ProbeRegistry registry;
    std::vector<ProbePhase> hits;
    registry.setHitCallback([&](const std::string&, ProbePhase phase) {
        hits.push_back(phase);
    });

    ProbeRegistry::Registration enter =
        registry.registerMethod(kModule, kMethod, "App.Query", ProbeEvents::Enter);
    ProbeRegistry::Registration exit =
        registry.registerMethod(kModule, kMethod, "App.Query", ProbeEvents::Exit);

    ASSERT_TRUE(enter.inserted);
    ASSERT_FALSE(exit.inserted);
    ASSERT_TRUE(exit.changed);
    EXPECT_EQ(exit.plan.cookie, enter.plan.cookie);
    EXPECT_TRUE(exit.plan.onEnter());
    EXPECT_TRUE(exit.plan.onExit());

    ProbeRegistry::dispatch(exit.plan.cookie, ProbePhase::Enter);
    ProbeRegistry::dispatch(exit.plan.cookie, ProbePhase::Exit);
    ProbeRegistry::dispatch(exit.plan.cookie, ProbePhase::Exit);

    EXPECT_EQ(hits, (std::vector<ProbePhase>{ProbePhase::Enter, ProbePhase::Exit}));
}

TEST(ProbeRegistry, ReturnDoesNotFireForExceptionalExit) {
    ProbeRegistry registry;
    std::vector<ProbePhase> hits;
    registry.setHitCallback([&](const std::string&, ProbePhase phase) {
        hits.push_back(phase);
    });
    ProbePlan plan =
        registry.registerMethod(kModule, kMethod, "App.Main", ProbeEvents::Return).plan;

    ProbeRegistry::dispatch(plan.cookie, ProbePhase::Exit);
    ProbeRegistry::dispatch(plan.cookie, ProbePhase::Return);

    EXPECT_EQ(hits, (std::vector<ProbePhase>{ProbePhase::Return}));
}

TEST(ProbeRegistry, ConcurrentCallsFireAOneShotHookOnce) {
    ProbeRegistry registry;
    std::atomic<int> hits{0};
    registry.setHitCallback([&](const std::string&, ProbePhase) {
        hits.fetch_add(1, std::memory_order_relaxed);
    });
    ProbePlan plan =
        registry.registerMethod(kModule, kMethod, "App.Query", ProbeEvents::Enter).plan;

    std::vector<std::thread> callers;
    for (int i = 0; i < 16; ++i) {
        callers.emplace_back([plan] {
            ProbeRegistry::dispatch(plan.cookie, ProbePhase::Enter);
        });
    }
    for (std::thread& caller : callers) {
        caller.join();
    }

    EXPECT_EQ(hits.load(std::memory_order_relaxed), 1);
}

TEST(ProbeRegistry, CompletedProbeCanBeRearmedWithANewStableCookie) {
    ProbeRegistry registry;
    int hits = 0;
    registry.setHitCallback([&](const std::string&, ProbePhase) {
        ++hits;
    });

    ProbePlan first =
        registry.registerMethod(kModule, kMethod, "App.Query", ProbeEvents::Enter).plan;
    ProbeRegistry::dispatch(first.cookie, ProbePhase::Enter);
    ASSERT_EQ(hits, 1);

    ProbeRegistry::Registration second =
        registry.registerMethod(kModule, kMethod, "App.Query", ProbeEvents::Enter);
    ASSERT_TRUE(second.changed);
    ASSERT_TRUE(second.inserted);
    ASSERT_NE(second.plan.cookie, first.cookie);

    ProbeRegistry::dispatch(first.cookie, ProbePhase::Enter);
    ProbeRegistry::dispatch(second.plan.cookie, ProbePhase::Enter);
    EXPECT_EQ(hits, 2);
}

TEST(ProbeRegistry, RemovedModulesDeactivateEmbeddedCookies) {
    ProbeRegistry registry;
    int hits = 0;
    registry.setHitCallback([&](const std::string&, ProbePhase) {
        ++hits;
    });
    ProbePlan plan =
        registry.registerMethod(kModule, kMethod, "App.Query", ProbeEvents::Enter).plan;

    registry.removeModule(kModule);
    EXPECT_FALSE(registry.planFor(kModule, kMethod));

    ProbeRegistry::dispatch(plan.cookie, ProbePhase::Enter);
    EXPECT_EQ(hits, 0);
}
