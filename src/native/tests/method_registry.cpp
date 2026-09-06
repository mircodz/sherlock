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

MethodRegistry::Resolution resolved(std::string symbol = "Example.Owner.Method") {
    return {std::move(symbol), S_OK, {}};
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
    EXPECT_EQ(registry.name(frame), "Example.Owner.Method [m1:06000001]");
}

TEST(MethodRegistry, ModulesAndOverloadTokensHaveDistinctCookiesAndNames) {
    MethodRegistry registry([](ModuleID, mdMethodDef) { return resolved(); });
    registry.moduleLoaded(kModule);
    registry.moduleLoaded(kModule + 1);
    FrameId first = registry.intern(kModule, kMethod);
    FrameId overload = registry.intern(kModule, kMethod + 1);
    FrameId otherModule = registry.intern(kModule + 1, kMethod);
    EXPECT_LT(first, overload);
    EXPECT_LT(overload, otherModule);
    EXPECT_NE(registry.name(first), registry.name(overload));
    EXPECT_NE(registry.name(first), registry.name(otherModule));
    EXPECT_EQ(registry.name(first), "Example.Owner.Method [m1:06000001]");
    EXPECT_EQ(registry.name(overload), "Example.Owner.Method [m1:06000002]");
    EXPECT_EQ(registry.name(otherModule), "Example.Owner.Method [m2:06000001]");
}

TEST(MethodRegistry, ReloadedModuleRetainsHistoricalNamesAndGetsFreshCookies) {
    std::string symbol = "Original.Type.Method";
    MethodRegistry registry([&](ModuleID, mdMethodDef) { return resolved(symbol); });
    registry.moduleLoaded(kModule);
    FrameId original = registry.intern(kModule, kMethod);
    std::string originalName = registry.name(original);
    registry.moduleUnloaded(kModule);
    EXPECT_EQ(registry.name(original), originalName);
    EXPECT_THROW(registry.intern(kModule, kMethod), std::logic_error);

    symbol = "Replacement.Type.Method";
    registry.moduleLoaded(kModule);
    FrameId replacement = registry.intern(kModule, kMethod);
    EXPECT_GT(replacement, original);
    EXPECT_EQ(registry.name(original), originalName);
    EXPECT_TRUE(registry.name(replacement).starts_with(symbol));
}

TEST(MethodRegistry, SameNamedDefinitionInReloadedModuleDoesNotAliasOldName) {
    MethodRegistry registry([](ModuleID, mdMethodDef) { return resolved(); });
    registry.moduleLoaded(kModule);
    FrameId old = registry.intern(kModule, kMethod);
    registry.moduleUnloaded(kModule);
    registry.moduleLoaded(kModule);
    FrameId current = registry.intern(kModule, kMethod);
    EXPECT_NE(old, current);
    EXPECT_NE(registry.name(old), registry.name(current));
    EXPECT_TRUE(registry.name(old).ends_with("[m1:06000001]"));
    EXPECT_TRUE(registry.name(current).ends_with("[m2:06000001]"));
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
    std::string failure = registry.name(frame);
    EXPECT_TRUE(failure.starts_with("<unresolved method "));
    EXPECT_NE(failure.find("GetModuleMetaData"), std::string::npos);
    EXPECT_NE(failure.find("HRESULT=0x80004005"), std::string::npos);
    EXPECT_EQ(registry.intern(kModule, kMethod), frame);
    EXPECT_EQ(calls, 2);
    EXPECT_TRUE(registry.name(frame).starts_with("Example.Owner.Method "));
    EXPECT_TRUE(failure.starts_with("<unresolved method "));
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
    std::string firstName = registry.name(first);
    EXPECT_NE(firstName, registry.name(second));
    registry.moduleUnloaded(kModule);
    EXPECT_EQ(registry.name(first), firstName);
    EXPECT_EQ(calls, 2);
    registry.moduleLoaded(kModule);
    FrameId third = registry.intern(kModule, kMethod);
    EXPECT_NE(firstName, registry.name(third));
    EXPECT_EQ(calls, 3);
}

TEST(MethodRegistry, MissingProfilerProducesAttributableDistinctFailures) {
    MethodRegistry registry(nullptr, nullptr);
    registry.moduleLoaded(kModule);
    FrameId first = registry.intern(kModule, kMethod);
    FrameId second = registry.intern(kModule, kMethod + 1);
    EXPECT_NE(registry.name(first), registry.name(second));
    EXPECT_NE(registry.name(first).find("GetModuleMetaData/profiler"), std::string::npos);
    EXPECT_NE(registry.name(first).find("module=0x1000"), std::string::npos);
}

TEST(MethodRegistry, UnregisteredFramesIncludingZeroNeverShareSilentUnknown) {
    MethodRegistry registry([](ModuleID, mdMethodDef) { return resolved(); });
    EXPECT_EQ(registry.name(0), "<unregistered method frame=0>");
    EXPECT_NE(registry.name(0), registry.name(1));
    EXPECT_NE(registry.name(1), registry.name(2));
    EXPECT_THROW(registry.intern(kModule, kMethod), std::logic_error);
    EXPECT_THROW(registry.moduleLoaded(0), std::invalid_argument);
    registry.moduleLoaded(kModule);
    EXPECT_THROW(registry.intern(kModule, mdMethodDefNil), std::invalid_argument);
    EXPECT_THROW(registry.intern(kModule, 0x02000001), std::invalid_argument);
}

TEST(MethodRegistry, GenericDeclarationsAndOverloadSymbolsArePreserved) {
    MethodRegistry registry([](ModuleID, mdMethodDef token) {
        return resolved(token == kMethod
            ? "Example.Outer<T>+Inner<U>.Allocate<TValue>(System.Int32)"
            : "Example.Outer<T>+Inner<U>.Allocate<TValue>(System.String)");
    });
    registry.moduleLoaded(kModule);
    FrameId first = registry.intern(kModule, kMethod);
    FrameId second = registry.intern(kModule, kMethod + 1);
    EXPECT_TRUE(registry.name(first).starts_with("Example.Outer<T>+Inner<U>.Allocate<TValue>(System.Int32) "));
    EXPECT_TRUE(registry.name(second).starts_with("Example.Outer<T>+Inner<U>.Allocate<TValue>(System.String) "));
    EXPECT_NE(registry.name(first), registry.name(second));
}

TEST(MethodRegistry, LongUnicodeSymbolsAreOwnedAndNotTruncated) {
    std::string symbol = "名字.外側<T>+内側.割り当て🚀" + std::string(4096, 'x');
    MethodRegistry registry([&](ModuleID, mdMethodDef) { return resolved(symbol); });
    registry.moduleLoaded(kModule);
    FrameId frame = registry.intern(kModule, kMethod);
    EXPECT_TRUE(registry.name(frame).starts_with(symbol + " "));
    std::string original = registry.name(frame);
    symbol.assign("changed");
    EXPECT_EQ(registry.name(frame), original);
}

TEST(MethodRegistry, EscapesControlsAndSemicolonsWithoutIntroducingFoldedFrames) {
    std::string symbol = "Example.Type.Method;\n\r\t\x1b[31m\\literal";
    symbol.push_back('\0');
    symbol += "end";
    MethodRegistry registry([&](ModuleID, mdMethodDef) { return resolved(symbol); });
    registry.moduleLoaded(kModule);
    FrameId frame = registry.intern(kModule, kMethod);
    std::string label = registry.name(frame);
    EXPECT_EQ(label, "Example.Type.Method\\x3b\\x0a\\x0d\\x09\\x1b[31m\\\\literal\\x00end [m1:06000001]");

    std::string folded = "root;" + label + ";leaf 1\n";
    EXPECT_EQ(std::count(folded.begin(), folded.end(), ';'), 2);
    EXPECT_EQ(std::count(folded.begin(), folded.end(), '\n'), 1);
}

TEST(MethodRegistry, EscapesEveryAsciiControlButPreservesUnicode) {
    std::string symbol = "名字.外側.Method";
    for (unsigned char byte = 0; byte < 0x20; ++byte) {
        symbol.push_back(static_cast<char>(byte));
    }
    symbol.push_back(0x7f);
    MethodRegistry registry([&](ModuleID, mdMethodDef) { return resolved(symbol); });
    registry.moduleLoaded(kModule);
    std::string label = registry.name(registry.intern(kModule, kMethod));
    EXPECT_TRUE(label.starts_with("名字.外側.Method\\x00\\x01"));
    EXPECT_NE(label.find("\\x1f\\x7f"), std::string::npos);
    for (unsigned char byte : label) {
        EXPECT_GE(byte, 0x20);
        EXPECT_NE(byte, 0x7f);
    }
}

TEST(MethodRegistry, EscapedMetadataDoesNotLookLikeLiteralEscapeSequences) {
    MethodRegistry registry([](ModuleID, mdMethodDef token) {
        return resolved(token == kMethod ? "Example.Type.Method;" : "Example.Type.Method\\x3b");
    });
    registry.moduleLoaded(kModule);
    EXPECT_EQ(registry.name(registry.intern(kModule, kMethod)), "Example.Type.Method\\x3b [m1:06000001]");
    EXPECT_EQ(registry.name(registry.intern(kModule, kMethod + 1)), "Example.Type.Method\\\\x3b [m1:06000002]");
}

TEST(MethodRegistry, UnresolvedDiagnosticsEscapeUntrustedStagesAndKeepFullIdentity) {
    MethodRegistry registry([](ModuleID, mdMethodDef) {
        return MethodRegistry::Resolution{{}, E_FAIL, "GetMethodProps;\n\x1b"};
    });
    registry.moduleLoaded(kModule);
    std::string label = registry.name(registry.intern(kModule, kMethod));
    EXPECT_EQ(label, "<unresolved method frame=1 [module=0x1000 token=0x06000001 lifetime=1] "
                     "stage=GetMethodProps\\x3b\\x0a\\x1b HRESULT=0x80004005>");
}

TEST(MethodRegistry, EmptySuccessfulResolutionIsAnExplicitRetryableFailure) {
    int calls = 0;
    MethodRegistry registry([&](ModuleID, mdMethodDef) {
        ++calls;
        return calls == 1 ? MethodRegistry::Resolution{} : resolved();
    });
    registry.moduleLoaded(kModule);
    FrameId frame = registry.intern(kModule, kMethod);
    EXPECT_NE(registry.name(frame).find("resolver/empty-symbol"), std::string::npos);
    EXPECT_EQ(registry.intern(kModule, kMethod), frame);
    EXPECT_TRUE(registry.name(frame).starts_with("Example.Owner.Method "));
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
    EXPECT_TRUE(registry.name(frame).starts_with("Example.Owner.Method "));
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
        EXPECT_TRUE(active->name(reentered).starts_with("<unresolved method "));
        return resolved();
    });
    active = &registry;
    registry.moduleLoaded(kModule);
    FrameId frame = registry.intern(kModule, kMethod);
    EXPECT_EQ(frame, reentered);
    EXPECT_EQ(calls, 1);
    EXPECT_TRUE(registry.name(frame).starts_with("Example.Owner.Method "));
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
            return resolved("Stale.Type.Method");
        }
        return resolved("Current.Type.Method");
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
    EXPECT_TRUE(registry.name(current).starts_with("Current.Type.Method "));
    EXPECT_NE(registry.name(old).find("module-lifetime-ended-during-resolution"), std::string::npos);
    EXPECT_EQ(registry.name(old).find("Stale.Type.Method"), std::string::npos);
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
                    EXPECT_FALSE(registry.name(frames[worker][method]).empty());
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
        EXPECT_TRUE(registry.name(frame).starts_with("Example.Owner.Method "));
    }
}
