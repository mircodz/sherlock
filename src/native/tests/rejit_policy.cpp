#include "sherlock/profiler/rejit_policy.hpp"
#include "sherlock/profiler/probe.hpp"
#include "sherlock/profiler/shadowstack.hpp"

#include <gtest/gtest.h>

#include <string>
#include <utility>

using namespace Sherlock;

TEST(RejitPolicy, ExcludesOnlyTheCoreLibArrayEntryHelpers) {
    for (const std::string method : {"StelemRef", "LdelemaRef"}) {
        const char* reason = rejit::rejection("System.Private.CoreLib", "System.Runtime.CompilerServices.CastHelpers", method);
        ASSERT_NE(reason, nullptr);
        EXPECT_NE(std::string(reason).find("CLR-owned array helper"), std::string::npos);
        EXPECT_EQ(rejit::rejection("Application", "System.Runtime.CompilerServices.CastHelpers", method), nullptr);
        EXPECT_EQ(rejit::rejection("System.Private.CoreLib", "Application.CastHelpers", method), nullptr);
        EXPECT_EQ(rejit::rejection("System.Private.CoreLib", "System.Runtime.CompilerServices.CastHelpers", method + "Other"), nullptr);
    }
}

TEST(RejitPolicy, ExcludesTheMethodsTheJitCallsAsHelpers) {
    for (const auto& [type, method] : {
             std::pair{"System.Runtime.CompilerServices.CastHelpers", "ChkCastClass"},
             std::pair{"System.Runtime.CompilerServices.CastHelpers", "Box"},
             std::pair{"System.Math", "ConvertToInt32Checked"},
             std::pair{"System.SpanHelpers", "Memmove"},
             std::pair{"Internal.Runtime.CompilerHelpers.ThrowHelpers", "ThrowAmbiguousResolutionException"},
             std::pair{"System.Runtime.CompilerServices.StaticsHelpers", "GetOptimizedNonGCThreadStaticBase"},
             std::pair{"System.Threading.Monitor", "Enter"},
         }) {
        const char* reason = rejit::rejection("System.Private.CoreLib", type, method);
        ASSERT_NE(reason, nullptr) << type << "." << method;
        EXPECT_NE(std::string(reason).find("JIT helper"), std::string::npos);
        EXPECT_EQ(rejit::rejection("Application", type, method), nullptr);
    }
}

TEST(RejitPolicy, ExcludesRuntimeAsyncMethods) {
    EXPECT_NE(rejit::rejection(DWORD{0x2000}), nullptr);
    EXPECT_NE(rejit::rejection(DWORD{0x2008}), nullptr);
    EXPECT_EQ(rejit::rejection(DWORD{0x0008}), nullptr); // NoInlining alone
    EXPECT_EQ(rejit::rejection(DWORD{0}), nullptr);
}

TEST(RejitPolicy, KeepsOrdinaryFrameworkAndUserMethodsEligible) {
    EXPECT_EQ(rejit::rejection("System.Private.CoreLib", "System.Collections.Generic.List`1", "Add"), nullptr);
    EXPECT_EQ(rejit::rejection("System.Private.CoreLib", "System.Math", "Abs"), nullptr);
    EXPECT_EQ(rejit::rejection("System.Private.CoreLib", "System.Environment", "InitializeCommandLineArgs"), nullptr);
    EXPECT_EQ(rejit::rejection("System.Private.CoreLib", "Application.Monitor", "Enter"), nullptr);
    EXPECT_EQ(rejit::rejection("Application", "Application.Program", "Main"), nullptr);
}

TEST(RejitPolicy, UnavailableMetadataDoesNotPermitARejitRequest) {
    EXPECT_NE(rejit::rejection(static_cast<IMetaDataImport*>(nullptr), 0x06000001), nullptr);
    EXPECT_NE(rejit::rejection(static_cast<ICorProfilerInfo10*>(nullptr), 1, 0x06000001), nullptr);
}

TEST(RejitPolicy, DirectProbeRegistrationCannotBypassEligibility) {
    ProbeManager probes(nullptr, nullptr);
    EXPECT_FALSE(probes.registerMethod(1, 0x06000001, "Unknown.Method", ProbeEvents::Enter));
    EXPECT_FALSE(probes.planFor(1, 0x06000001));
}

TEST(RejitPolicy, RewriteGuardRunsBeforeFrameRegistrationAndIlPublication) {
    MethodRegistry methods(nullptr, nullptr);
    ShadowStackInstrumenter instrumenter(nullptr, nullptr, methods);
    EXPECT_FALSE(instrumenter.rewrite(1, 0x06000001, {}, nullptr));
    EXPECT_EQ(instrumenter.skippedCount(), 1);
    EXPECT_EQ(instrumenter.instrumentedCount(), 0);
}
