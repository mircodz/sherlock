#include "sherlock/profiler/process_filter.hpp"

#include <gtest/gtest.h>

#include <string>
#include <vector>

using Sherlock::process_filter::select;

TEST(ProcessFilter, DirectManagedEntryUsesOnlyItsBasename) {
    const std::vector<std::string> arguments{"dotnet", "/work/output/Sherlock.Core.Tests.dll", "Other.dll"};
    const auto result = select(arguments, "/usr/local/bin/dotnet", "Sherlock.*.dll");
    EXPECT_TRUE(result.included);
    EXPECT_EQ(result.name, "Sherlock.Core.Tests.dll");
    EXPECT_TRUE(result.error.empty());
    EXPECT_FALSE(select(arguments, "dotnet", "Other.dll").included);
    EXPECT_FALSE(select(arguments, "dotnet", "dotnet").included);
}

TEST(ProcessFilter, SdkCommandsNeverSelectLaterInputDlls) {
    for (const std::string command : {"test", "run", "build", "msbuild", "vstest", "publish", "restore", "tool", "custom-command"}) {
        SCOPED_TRACE(command);
        const std::vector<std::string> arguments{"dotnet", command, "/work/Sherlock.Core.Tests.dll"};
        const auto result = select(arguments, "/usr/bin/dotnet", "Sherlock.*.dll");
        EXPECT_FALSE(result.included);
        EXPECT_EQ(result.name, "dotnet");
        EXPECT_TRUE(result.error.empty());
        EXPECT_TRUE(select(arguments, "dotnet", "dotnet").included);
        EXPECT_FALSE(select(arguments, "dotnet", "dotnet.dll").included);
    }
}

TEST(ProcessFilter, ExecSkipsEveryKnownHostOptionValue) {
    for (const std::string option : {
        "--runtimeconfig", "--depsfile", "--additionalprobingpath", "--additional-deps",
        "--roll-forward", "--fx-version", "--roll-forward-on-no-candidate-fx"
    }) {
        SCOPED_TRACE(option);
        for (const bool inlineValue : {false, true}) {
            SCOPED_TRACE(inlineValue);
            std::vector<std::string> arguments{"dotnet", "exec"};
            if (inlineValue) {
                arguments.push_back(option + "=/work/Sherlock.Decoy.dll");
            } else {
                arguments.push_back(option);
                arguments.push_back("/work/Sherlock.Decoy.dll");
            }
            arguments.push_back("/work/Actual.dll");
            EXPECT_FALSE(select(arguments, "dotnet", "Sherlock.*.dll").included);
            const auto result = select(arguments, "dotnet", "Actual.dll");
            EXPECT_TRUE(result.included);
            EXPECT_EQ(result.name, "Actual.dll");
            EXPECT_TRUE(result.error.empty());
        }
    }
}

TEST(ProcessFilter, ExecHandlesMixedHostOptionsBeforeEntry) {
    const std::vector<std::string> arguments{
        "dotnet", "exec", "--runtimeconfig", "/work/test.runtimeconfig.json",
        "--depsfile=/work/test.deps.json", "--additionalprobingpath", "/work/packages with spaces",
        "--roll-forward=LatestMajor", "--fx-version", "10.0.0", "/work/Sherlock.Core.Tests.dll"
    };
    const auto result = select(arguments, "dotnet", "Sherlock.*.dll");
    EXPECT_TRUE(result.included);
    EXPECT_EQ(result.name, "Sherlock.Core.Tests.dll");
    EXPECT_TRUE(result.error.empty());
}

TEST(ProcessFilter, DirectInvocationCanHaveHostOptions) {
    const std::vector<std::string> arguments{"dotnet", "--fx-version", "10.0.0", "Sherlock.Core.Tests.dll"};
    EXPECT_TRUE(select(arguments, "dotnet", "Sherlock.*.dll").included);
}

TEST(ProcessFilter, UnknownOptionsDoNotGuessLaterEntryAssemblies) {
    for (const std::vector<std::string>& arguments : {
        std::vector<std::string>{"dotnet", "--info", "Sherlock.Core.Tests.dll"},
        std::vector<std::string>{"dotnet", "exec", "--unknown", "Sherlock.Decoy.dll", "Sherlock.Core.Tests.dll"}
    }) {
        const auto result = select(arguments, "dotnet", "Sherlock.*.dll");
        EXPECT_FALSE(result.included);
        EXPECT_EQ(result.name, "dotnet");
        EXPECT_TRUE(result.error.empty());
    }
}

TEST(ProcessFilter, MissingHostOptionValuesFailClosed) {
    for (const std::vector<std::string>& arguments : {
        std::vector<std::string>{"dotnet", "exec", "--runtimeconfig"},
        std::vector<std::string>{"dotnet", "exec", "--runtimeconfig="},
        std::vector<std::string>{"dotnet", "exec", "--runtimeconfig", ""}
    }) {
        const auto result = select(arguments, "dotnet", "*");
        EXPECT_FALSE(result.included);
        EXPECT_FALSE(result.error.empty());
    }
}

TEST(ProcessFilter, ApphostHasConventionalManagedAlias) {
    const std::vector<std::string> arguments{"/work/Sherlock.Core.Tests"};
    const auto result = select(arguments, arguments.front(), "Sherlock.*.dll");
    EXPECT_TRUE(result.included);
    EXPECT_EQ(result.name, "Sherlock.Core.Tests.dll");
    EXPECT_TRUE(result.error.empty());
    EXPECT_EQ(select(arguments, arguments.front(), "Sherlock.Core.Tests").name, "Sherlock.Core.Tests");
}

TEST(ProcessFilter, ExplicitExecutableMatchTakesPrecedenceOverManagedAlias) {
    const std::vector<std::string> arguments{"Sherlock.Core.Tests.exe"};
    const auto result = select(arguments, arguments.front(), "Sherlock.*.dll\n*.exe");
    EXPECT_TRUE(result.included);
    EXPECT_EQ(result.name, "Sherlock.Core.Tests.exe");
    EXPECT_EQ(select(arguments, arguments.front(), "*").name, "Sherlock.Core.Tests.exe");
}

TEST(ProcessFilter, WindowsApphostUsesExeStemForAlias) {
    const std::vector<std::string> arguments{R"(C:\Program Files\Sherlock\Sherlock.Core.Tests.EXE)"};
    const auto result = select(arguments, arguments.front(), "sherlock.*.dll");
    EXPECT_TRUE(result.included);
    EXPECT_EQ(result.name, "Sherlock.Core.Tests.dll");
    EXPECT_TRUE(result.error.empty());
}

TEST(ProcessFilter, WindowsDotnetRecognizesManagedDllAndExe) {
    for (const std::string entry : {"Sherlock.Core.Tests.dll", "Sherlock.Core.Tests.exe"}) {
        const std::vector<std::string> arguments{R"(C:\Program Files\dotnet\dotnet.exe)", "C:\\work\\" + entry};
        const auto result = select(arguments, arguments.front(), "sherlock.*");
        EXPECT_TRUE(result.included);
        EXPECT_EQ(result.name, entry);
        EXPECT_TRUE(result.error.empty());
    }
    const std::vector<std::string> sdk{"dotnet.exe", "test", R"(C:\work\Sherlock.Core.Tests.dll)"};
    EXPECT_EQ(select(sdk, "dotnet.exe", "*").name, "dotnet.exe");
    EXPECT_FALSE(select(sdk, "dotnet.exe", "Sherlock.*.dll").included);
}

TEST(ProcessFilter, TokenizedArgumentsPreserveSpacesWithoutReparsingQuotes) {
    const std::vector<std::string> arguments{
        "/path with spaces/dotnet", "exec", "--runtimeconfig", "/path with spaces/Decoy.dll",
        "/test output/Sherlock Tests.dll", "--filter", "Name=\"A B\""
    };
    const auto result = select(arguments, arguments.front(), "Sherlock Tests.dll");
    EXPECT_TRUE(result.included);
    EXPECT_EQ(result.name, "Sherlock Tests.dll");
    EXPECT_FALSE(select(arguments, arguments.front(), "Decoy.dll").included);
}

TEST(ProcessFilter, MultiplePatternsAreAlternatives) {
    const std::vector<std::string> arguments{"dotnet", "Sherlock.Core.Tests.dll"};
    EXPECT_TRUE(select(arguments, "dotnet", "Other.dll\nSherlock.*.dll\nLast.dll").included);
    EXPECT_FALSE(select(arguments, "dotnet", "Other.dll\nLast.dll").included);
}

TEST(ProcessFilter, MatchingIsAsciiCaseInsensitiveAndAnchored) {
    const std::vector<std::string> arguments{"dotnet", "Sherlock.Core.Tests.DLL"};
    for (const std::string pattern : {"sHERLOCK.cORE.tESTS.dll", "sherlock.*.dll", "*", "*.DLL", "*Core*Tests*"}) {
        SCOPED_TRACE(pattern);
        EXPECT_TRUE(select(arguments, "dotnet", pattern).included);
    }
    for (const std::string pattern : {"Sherlock", "Core.Tests.DLL", "Sherlock.*.DLLx", "xSherlock.*", "*.exe"}) {
        SCOPED_TRACE(pattern);
        EXPECT_FALSE(select(arguments, "dotnet", pattern).included);
    }
}

TEST(ProcessFilter, QuestionMarkMatchesExactlyOneCharacterAndStarsCanBeEmpty) {
    const std::vector<std::string> arguments{"dotnet", "A.dll"};
    for (const std::string pattern : {"?.dll", "A*.dll", "**A**.dll**", "*?.dll", "A.dll*"}) {
        SCOPED_TRACE(pattern);
        EXPECT_TRUE(select(arguments, "dotnet", pattern).included);
    }
    for (const std::string pattern : {"??.dll", "A?.dll", "?.dll?", "A.dll?"}) {
        SCOPED_TRACE(pattern);
        EXPECT_FALSE(select(arguments, "dotnet", pattern).included);
    }
}

TEST(ProcessFilter, RegexAndCharacterClassSyntaxIsLiteral) {
    const std::vector<std::string> arguments{"dotnet", "A.dll"};
    EXPECT_FALSE(select(arguments, "dotnet", "[A].dll").included);
    EXPECT_FALSE(select(arguments, "dotnet", "(A).dll").included);
    EXPECT_FALSE(select(arguments, "dotnet", "A.+dll").included);
    const std::vector<std::string> literal{"dotnet", "[A].dll"};
    EXPECT_TRUE(select(literal, "dotnet", "[A].dll").included);
}

TEST(ProcessFilter, InvalidPatternsFailEvenWhenAnEarlierPatternMatches) {
    std::vector<std::string> invalid{
        "", " ", "\nA.dll", "A.dll\n", "A.dll\n\nOther.dll", "*.dll\n ",
        "/A.dll", R"(C:\A.dll)", R"(folder\*.dll)", "folder/*.dll"
    };
    for (unsigned char control = 0; control <= 0x20; ++control) {
        if (control != '\n' && control != ' ') {
            invalid.push_back("*.dll\nA" + std::string(1, static_cast<char>(control)) + ".dll");
        }
    }
    invalid.push_back("A" + std::string(1, '\x7f') + ".dll");
    const std::vector<std::string> arguments{"dotnet", "A.dll"};
    for (const auto& pattern : invalid) {
        SCOPED_TRACE(pattern);
        const auto result = select(arguments, "dotnet", pattern);
        EXPECT_FALSE(result.included);
        EXPECT_FALSE(result.error.empty());
    }
}

TEST(ProcessFilter, NonMatchIsNotAnError) {
    const std::vector<std::string> arguments{"dotnet", "Other.dll"};
    const auto result = select(arguments, "dotnet", "Sherlock.*.dll");
    EXPECT_FALSE(result.included);
    EXPECT_EQ(result.name, "Other.dll");
    EXPECT_TRUE(result.error.empty());
}

TEST(ProcessFilter, UnicodeLiteralsAreExactAndQuestionMarkAdvancesOneCodePoint) {
    const std::vector<std::string> arguments{"dotnet", "测试.é🚀.dll"};
    for (const std::string pattern : {"测试.é🚀.dll", "??.??.DLL", "测试.?🚀.dll", "*🚀.dll", "*.dll"}) {
        SCOPED_TRACE(pattern);
        EXPECT_TRUE(select(arguments, "dotnet", pattern).included);
    }
    for (const std::string pattern : {"测试.É🚀.dll", "??.?.dll", "测试.é?.dll?", "???.??.dll"}) {
        SCOPED_TRACE(pattern);
        EXPECT_FALSE(select(arguments, "dotnet", pattern).included);
    }
}

TEST(ProcessFilter, MalformedUtf8FallsBackToIndividualBytes) {
    const std::vector<std::string> arguments{"dotnet", std::string("\xc0\xaf") + ".dll"};
    EXPECT_TRUE(select(arguments, "dotnet", "??.dll").included);
    EXPECT_FALSE(select(arguments, "dotnet", "?.dll").included);
}

TEST(ProcessFilter, SuppliedExecutableTakesPrecedenceOverArgvZero) {
    const std::vector<std::string> arguments{"Sherlock.Core.Tests", "Sherlock.Decoy.dll"};
    const auto result = select(arguments, "/usr/bin/dotnet", "Sherlock.*.dll");
    EXPECT_TRUE(result.included);
    EXPECT_EQ(result.name, "Sherlock.Decoy.dll");
    const std::vector<std::string> sdk{"Sherlock.Core.Tests", "test", "Sherlock.Decoy.dll"};
    EXPECT_FALSE(select(sdk, "/usr/bin/dotnet", "Sherlock.*.dll").included);
}

TEST(ProcessFilter, MissingExecutableFallsBackOnlyToArgvZero) {
    const std::vector<std::string> arguments{"/work/Sherlock.Core.Tests"};
    EXPECT_TRUE(select(arguments, "", "Sherlock.*.dll").included);
    EXPECT_TRUE(select({}, "/work/Sherlock.Core.Tests", "Sherlock.*.dll").included);
    for (const std::string executable : {"", "/", R"(C:\)"}) {
        const auto result = select({}, executable, "*");
        EXPECT_FALSE(result.included);
        EXPECT_FALSE(result.error.empty());
    }
}

TEST(ProcessFilter, RenamedAndCustomHostsDoNotDiscoverHostedLibraries) {
    const std::vector<std::string> arguments{"/work/custom-host", "/work/Sherlock.Core.Tests.dll"};
    const auto result = select(arguments, arguments.front(), "Sherlock.*.dll");
    EXPECT_FALSE(result.included);
    EXPECT_EQ(result.name, "custom-host");
    EXPECT_TRUE(result.error.empty());
    EXPECT_TRUE(select(arguments, arguments.front(), "custom-host").included);
    EXPECT_EQ(select(arguments, arguments.front(), "custom-host.dll").name, "custom-host.dll");
}

TEST(ProcessFilter, CurrentProcessCanReadItsIdentity) {
    const auto result = Sherlock::process_filter::current("*");
    EXPECT_TRUE(result.included) << result.error;
    EXPECT_FALSE(result.name.empty());
    EXPECT_TRUE(result.error.empty());
}
