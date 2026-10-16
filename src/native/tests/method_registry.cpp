#include "sherlock/profiler/method_registry.hpp"

#include <gtest/gtest.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <barrier>
#include <future>
#include <set>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

using namespace Sherlock;

namespace {

constexpr ModuleID kModule = 0x1000;
constexpr mdMethodDef kMethod = 0x06000001;

MethodName named(std::string type = "Owner", std::string method = "Method", std::string parameters = "()") {
    MethodName name;
    name.mvid = {0x5e};
    name.module = "Example.dll";
    name.ns = "Example";
    name.type = std::move(type);
    name.name = std::move(method);
    name.parameters = name.qualifiedParameters = std::move(parameters);
    name.returnType = name.qualifiedReturnType = "void";
    return name;
}

MethodRegistry::Resolution resolved(MethodName name = named()) {
    return {std::move(name), S_OK, {}};
}

// "Example.Owner.Method()" when resolved, otherwise the problem.
std::string text(const MethodRegistry::Method& method) {
    if (!method.problem.empty()) {
        return "problem: " + method.problem;
    }
    return method.name.ns + "." + method.name.type + "." + method.name.name + method.name.genericParameters +
           method.name.parameters;
}

std::string text(const MethodRegistry& registry, FrameId frame) {
    return text(registry.describe(frame));
}

} // namespace

TEST(MethodRegistry, RepeatedRegistrationAndModuleLoadKeepOneCookie) {
    int calls = 0;
    MethodRegistry registry([&](ModuleID module, mdMethodDef token) {
        EXPECT_EQ(module, kModule);
        EXPECT_EQ(token, kMethod);
        ++calls;
        return resolved();
    });
    registry.moduleLoaded(kModule);
    FrameId frame = registry.intern(kModule, kMethod);
    ASSERT_NE(frame, 0);
    registry.moduleLoaded(kModule);
    EXPECT_EQ(registry.intern(kModule, kMethod), frame);
    EXPECT_EQ(calls, 1);
    EXPECT_EQ(text(registry, frame), "Example.Owner.Method()");
}

TEST(MethodRegistry, ModulesAndOverloadTokensHaveDistinctCookies) {
    MethodRegistry registry([](ModuleID, mdMethodDef token) {
        return resolved(named("Owner", "Method", token == kMethod ? "(int)" : "(string)"));
    });
    registry.moduleLoaded(kModule);
    registry.moduleLoaded(kModule + 1);
    FrameId first = registry.intern(kModule, kMethod);
    FrameId overload = registry.intern(kModule, kMethod + 1);
    FrameId otherModule = registry.intern(kModule + 1, kMethod);
    EXPECT_LT(first, overload);
    EXPECT_LT(overload, otherModule);
    EXPECT_EQ(text(registry, first), "Example.Owner.Method(int)");
    EXPECT_EQ(text(registry, overload), "Example.Owner.Method(string)");
    EXPECT_EQ(text(registry, otherModule), "Example.Owner.Method(int)");
}

TEST(MethodRegistry, ReloadedModuleRetainsHistoricalNamesAndGetsFreshCookies) {
    std::string type = "Original";
    MethodRegistry registry([&](ModuleID, mdMethodDef) { return resolved(named(type)); });
    registry.moduleLoaded(kModule);
    FrameId original = registry.intern(kModule, kMethod);
    std::string originalName = text(registry, original);
    registry.moduleUnloaded(kModule);
    EXPECT_EQ(text(registry, original), originalName);
    EXPECT_THROW(registry.intern(kModule, kMethod), std::logic_error);

    type = "Replacement";
    registry.moduleLoaded(kModule);
    FrameId replacement = registry.intern(kModule, kMethod);
    EXPECT_GT(replacement, original);
    EXPECT_EQ(text(registry, original), originalName);
    EXPECT_EQ(text(registry, replacement), "Example.Replacement.Method()");
}

TEST(MethodRegistry, ReloadedDefinitionGetsFreshCookieButKeepsItsName) {
    MethodRegistry registry([](ModuleID, mdMethodDef) { return resolved(); });
    registry.moduleLoaded(kModule);
    FrameId old = registry.intern(kModule, kMethod);
    registry.moduleUnloaded(kModule);
    registry.moduleLoaded(kModule);
    FrameId current = registry.intern(kModule, kMethod);
    EXPECT_NE(old, current);
    EXPECT_EQ(text(registry, old), "Example.Owner.Method()");
    EXPECT_EQ(text(registry, current), "Example.Owner.Method()");
}

TEST(MethodRegistry, FailedResolutionRetriesSameCookieAndReturnsIndependentCopies) {
    int calls = 0;
    MethodRegistry registry([&](ModuleID, mdMethodDef) {
        if (++calls == 1) {
            return MethodRegistry::Resolution{{}, E_FAIL, "GetModuleMetaData"};
        }
        return resolved();
    });
    registry.moduleLoaded(kModule);
    FrameId frame = registry.intern(kModule, kMethod);
    std::string failure = text(registry, frame);
    EXPECT_TRUE(failure.starts_with("problem: "));
    EXPECT_NE(failure.find("GetModuleMetaData"), std::string::npos);
    EXPECT_NE(failure.find("HRESULT 0x80004005"), std::string::npos);
    EXPECT_EQ(registry.intern(kModule, kMethod), frame);
    EXPECT_EQ(calls, 2);
    EXPECT_EQ(text(registry, frame), "Example.Owner.Method()");
    EXPECT_TRUE(failure.starts_with("problem: "));
    EXPECT_EQ(registry.intern(kModule, kMethod), frame);
    EXPECT_EQ(calls, 2);
}

TEST(MethodRegistry, FailedDefinitionsRemainDistinctAndSerializationDoesNotRetry) {
    int calls = 0;
    MethodRegistry registry([&](ModuleID, mdMethodDef) {
        ++calls;
        return MethodRegistry::Resolution{{}, E_FAIL, "GetMethodProps"};
    });
    registry.moduleLoaded(kModule);
    FrameId first = registry.intern(kModule, kMethod);
    FrameId second = registry.intern(kModule, kMethod + 1);
    std::string firstName = text(registry, first);
    EXPECT_NE(firstName, text(registry, second));
    registry.moduleUnloaded(kModule);
    EXPECT_EQ(text(registry, first), firstName);
    EXPECT_EQ(calls, 2);
    registry.moduleLoaded(kModule);
    FrameId third = registry.intern(kModule, kMethod);
    EXPECT_NE(firstName, text(registry, third));
    EXPECT_EQ(calls, 3);
}

TEST(MethodRegistry, MissingProfilerProducesAttributableDistinctFailures) {
    MethodRegistry registry(nullptr, nullptr);
    registry.moduleLoaded(kModule);
    FrameId first = registry.intern(kModule, kMethod);
    FrameId second = registry.intern(kModule, kMethod + 1);
    EXPECT_NE(text(registry, first), text(registry, second));
    EXPECT_NE(text(registry, first).find("GetModuleMetaData/profiler"), std::string::npos);
    EXPECT_NE(text(registry, first).find("module 0x1000"), std::string::npos);
    EXPECT_EQ(registry.describe(first).name.mvid, (std::array<std::uint8_t, 16>{}));
}

TEST(MethodRegistry, UnregisteredFramesIncludingZeroNeverShareSilentUnknown) {
    MethodRegistry registry([](ModuleID, mdMethodDef) { return resolved(); });
    EXPECT_EQ(text(registry, 0), "problem: frame 0 was never registered");
    EXPECT_NE(text(registry, 0), text(registry, 1));
    EXPECT_NE(text(registry, 1), text(registry, 2));
    EXPECT_THROW(registry.intern(kModule, kMethod), std::logic_error);
    EXPECT_THROW(registry.moduleLoaded(0), std::invalid_argument);
    registry.moduleLoaded(kModule);
    EXPECT_THROW(registry.intern(kModule, mdMethodDefNil), std::invalid_argument);
    EXPECT_THROW(registry.intern(kModule, 0x02000001), std::invalid_argument);
}

TEST(MethodRegistry, DescribesEveryNamePartAndTheIdentity) {
    MethodRegistry registry([](ModuleID, mdMethodDef token) {
        MethodName name = named("Outer<T>+Inner<U>", "Allocate", token == kMethod ? "(int)" : "(string)");
        name.genericParameters = "<TValue>";
        name.qualifiedParameters = token == kMethod ? "(System.Int32)" : "(System.String)";
        return resolved(std::move(name));
    });
    registry.moduleLoaded(kModule);
    MethodRegistry::Method first = registry.describe(registry.intern(kModule, kMethod));
    MethodRegistry::Method second = registry.describe(registry.intern(kModule, kMethod + 1));
    EXPECT_EQ(text(first), "Example.Outer<T>+Inner<U>.Allocate<TValue>(int)");
    EXPECT_EQ(first.token, kMethod);
    EXPECT_EQ(second.token, kMethod + 1);
    EXPECT_EQ(first.name.mvid, (std::array<std::uint8_t, 16>{0x5e}));
    EXPECT_EQ(first.name.module, "Example.dll");
    EXPECT_EQ(first.name.qualifiedParameters, "(System.Int32)");
    EXPECT_EQ(second.name.parameters, "(string)");
    EXPECT_TRUE(first.problem.empty());
}

TEST(MethodRegistry, LongUnicodeNamesAreOwnedAndNotTruncated) {
    std::string type = "外側<T>+内側" + std::string(4096, 'x');
    MethodRegistry registry([&](ModuleID, mdMethodDef) { return resolved(named(type, "割り当て🚀")); });
    registry.moduleLoaded(kModule);
    FrameId frame = registry.intern(kModule, kMethod);
    EXPECT_EQ(registry.describe(frame).name.type, type);
    std::string original = text(registry, frame);
    type.assign("changed");
    EXPECT_EQ(text(registry, frame), original);
}

TEST(MethodRegistry, KeepsMetadataBytesUnescaped) {
    std::string method = "Method;\n\r\t\x1b[31m\\literal";
    method.push_back('\0');
    method += "end";
    MethodRegistry registry([&](ModuleID, mdMethodDef) { return resolved(named("Type", method)); });
    registry.moduleLoaded(kModule);
    EXPECT_EQ(registry.describe(registry.intern(kModule, kMethod)).name.name, method);
}

TEST(MethodRegistry, UnresolvedProblemsNameTheStageAndTheMethod) {
    MethodRegistry registry([](ModuleID, mdMethodDef) {
        return MethodRegistry::Resolution{{}, E_FAIL, "GetMethodProps"};
    });
    registry.moduleLoaded(kModule);
    MethodRegistry::Method method = registry.describe(registry.intern(kModule, kMethod));
    EXPECT_EQ(method.problem, "GetMethodProps failed with HRESULT 0x80004005 (module 0x1000, token 0x06000001, lifetime 1)");
    EXPECT_EQ(method.token, kMethod);
    EXPECT_TRUE(method.name.name.empty());
    EXPECT_EQ(method.name.mvid, (std::array<std::uint8_t, 16>{}));
}

TEST(MethodRegistry, UnresolvedMethodsKeepTheirModulesIdentityButNoPartialName) {
    MethodRegistry registry([](ModuleID, mdMethodDef) {
        MethodName partial = named();
        return MethodRegistry::Resolution{std::move(partial), E_FAIL, "signature"};
    });
    registry.moduleLoaded(kModule);
    MethodRegistry::Method method = registry.describe(registry.intern(kModule, kMethod));
    EXPECT_FALSE(method.problem.empty());
    EXPECT_EQ(method.name.mvid, named().mvid);
    EXPECT_EQ(method.name.module, "Example.dll");
    EXPECT_TRUE(method.name.ns.empty());
    EXPECT_TRUE(method.name.type.empty());
    EXPECT_TRUE(method.name.name.empty());
    EXPECT_TRUE(method.name.parameters.empty());
}

TEST(MethodRegistry, EmptySuccessfulResolutionIsAnExplicitRetryableFailure) {
    int calls = 0;
    MethodRegistry registry([&](ModuleID, mdMethodDef) {
        ++calls;
        return calls == 1 ? MethodRegistry::Resolution{} : resolved();
    });
    registry.moduleLoaded(kModule);
    FrameId frame = registry.intern(kModule, kMethod);
    EXPECT_NE(text(registry, frame).find("resolver/empty-name"), std::string::npos);
    EXPECT_EQ(registry.intern(kModule, kMethod), frame);
    EXPECT_EQ(text(registry, frame), "Example.Owner.Method()");
}

TEST(MethodRegistry, ResolverExceptionsPropagateAndDoNotPreventRetry) {
    int calls = 0;
    MethodRegistry registry([&](ModuleID, mdMethodDef) {
        if (++calls == 1) {
            throw std::runtime_error("resolver failed");
        }
        return resolved();
    });
    registry.moduleLoaded(kModule);
    EXPECT_THROW(registry.intern(kModule, kMethod), std::runtime_error);
    FrameId frame = registry.intern(kModule, kMethod);
    EXPECT_EQ(frame, 1);
    EXPECT_EQ(text(registry, frame), "Example.Owner.Method()");
    EXPECT_EQ(calls, 2);
}

TEST(MethodRegistry, ResolverMayReenterRegistryWithoutDeadlockingOrRecursing) {
    MethodRegistry* active = nullptr;
    FrameId reentered = 0;
    int calls = 0;
    MethodRegistry registry([&](ModuleID module, mdMethodDef token) {
        ++calls;
        active->moduleLoaded(module);
        reentered = active->intern(module, token);
        EXPECT_TRUE(text(*active, reentered).starts_with("problem: "));
        return resolved();
    });
    active = &registry;
    registry.moduleLoaded(kModule);
    FrameId frame = registry.intern(kModule, kMethod);
    EXPECT_EQ(frame, reentered);
    EXPECT_EQ(calls, 1);
    EXPECT_EQ(text(registry, frame), "Example.Owner.Method()");
}

TEST(MethodRegistry, InFlightResolutionCannotPublishIntoReusedModuleLifetime) {
    std::promise<void> resolving;
    std::promise<void> finish;
    std::shared_future<void> released = finish.get_future().share();
    std::atomic<unsigned> calls{0};
    MethodRegistry registry([&](ModuleID, mdMethodDef) {
        if (calls.fetch_add(1) == 0) {
            resolving.set_value();
            released.wait();
            return resolved(named("Stale"));
        }
        return resolved(named("Current"));
    });
    registry.moduleLoaded(kModule);
    FrameId old = 0;
    std::thread worker([&] { old = registry.intern(kModule, kMethod); });
    resolving.get_future().wait();
    registry.moduleUnloaded(kModule);
    registry.moduleLoaded(kModule);
    FrameId current = registry.intern(kModule, kMethod);
    finish.set_value();
    worker.join();
    EXPECT_NE(old, current);
    EXPECT_EQ(text(registry, current), "Example.Current.Method()");
    EXPECT_NE(text(registry, old).find("module-lifetime-ended-during-resolution"), std::string::npos);
    EXPECT_EQ(text(registry, old).find("Stale"), std::string::npos);
}

TEST(MethodRegistry, ConcurrentRegistrationSharesCookiesAndKeepsResolvedNames) {
    constexpr unsigned workers = 8;
    constexpr unsigned methods = 16;
    std::atomic<unsigned> calls{0};
    std::barrier start(workers);
    MethodRegistry registry([&](ModuleID, mdMethodDef) {
        calls.fetch_add(1);
        std::this_thread::yield();
        return resolved();
    });
    registry.moduleLoaded(kModule);
    std::array<std::array<FrameId, methods>, workers> frames{};
    std::vector<std::thread> threads;
    for (unsigned worker = 0; worker < workers; ++worker) {
        threads.emplace_back([&, worker] {
            start.arrive_and_wait();
            for (unsigned pass = 0; pass < 20; ++pass) {
                for (unsigned method = 0; method < methods; ++method) {
                    frames[worker][method] = registry.intern(kModule, kMethod + method);
                    EXPECT_FALSE(text(registry, frames[worker][method]).empty());
                }
            }
        });
    }
    for (auto& thread : threads) {
        thread.join();
    }
    std::set<FrameId> unique(frames[0].begin(), frames[0].end());
    EXPECT_EQ(unique.size(), methods);
    EXPECT_EQ(calls.load(), methods);
    for (unsigned worker = 0; worker < workers; ++worker) {
        EXPECT_EQ(frames[worker], frames[0]);
    }
    for (FrameId frame : unique) {
        EXPECT_EQ(text(registry, frame), "Example.Owner.Method()");
    }
}
