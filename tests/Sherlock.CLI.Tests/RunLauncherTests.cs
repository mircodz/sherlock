using System.IO;
using Sherlock.Core.Collection;
using Spectre.Console;
using Xunit;

namespace Sherlock.CLI.Tests;

public sealed class RunLauncherTests
{
    private static RunOptions? Parse(params string[] args)
    {
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(TextWriter.Null),
            Ansi = AnsiSupport.No,
            Interactive = InteractionSupport.No,
        });
        return RunLauncher.Parse(args, console);
    }

    [Theory]
    [InlineData("--snapshot-on")]
    [InlineData("--profiler-log")]
    [InlineData("--include-process")]
    public void MissingOptionValuesAreRejected(string option)
    {
        Assert.Null(Parse(option));
        Assert.Null(Parse(option, "--"));
        Assert.Null(Parse(option, "--profile", "app"));
    }

    [Fact]
    public void DoubleDashForwardsEveryRemainingTokenLiterally()
    {
        RunOptions options = Assert.IsType<RunOptions>(Parse("--correlate", "--", "--profile", "--snapshot-on", "exit", "--", ""));
        Assert.Equal(new[] { "--profile", "--snapshot-on", "exit", "--", "" }, options.Command);
        Assert.True(options.Correlate);
        Assert.False(options.Profile);
        Assert.Null(options.SnapshotOn);
    }

    [Fact]
    public void OptionsAfterTheExecutableBelongToTheTarget()
    {
        RunOptions options = Assert.IsType<RunOptions>(Parse("--profile", "app", "--correlate", "--snapshot-on", "--", "arg"));
        Assert.Equal(new[] { "app", "--correlate", "--snapshot-on", "--", "arg" }, options.Command);
        Assert.True(options.Profile);
        Assert.False(options.Correlate);
        Assert.Null(options.SnapshotOn);
    }

    [Theory]
    [InlineData("TRACE", ProfilerLogLevel.Trace)]
    [InlineData("info", ProfilerLogLevel.Info)]
    [InlineData("warning", ProfilerLogLevel.Warning)]
    [InlineData("error", ProfilerLogLevel.Error)]
    [InlineData("off", ProfilerLogLevel.Off)]
    public void ParsesKnownProfilerLogLevels(string value, ProfilerLogLevel level)
    {
        Assert.Equal(level, Assert.IsType<RunOptions>(Parse("--profiler-log", value, "app")).ProfilerLogLevel);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("99")]
    [InlineData("-1")]
    public void RejectsInvalidProfilerLogLevels(string value)
    {
        Assert.Null(Parse("--profiler-log", value, "app"));
    }

    [Fact]
    public void UsesSharedOptionValidation()
    {
        Assert.Null(Parse());
        Assert.Null(Parse("--"));
        Assert.Null(Parse(""));
        Assert.Null(Parse("--experimental-gc-barrier", "app"));
        RunOptions options = Assert.IsType<RunOptions>(Parse("--experimental-gc-barrier", "--correlate", "app"));
        Assert.True(options.ExperimentalGcBarrier);
        Assert.True(options.UseGcBarrier);
    }

    [Fact]
    public void LiveAndUnknownOptionsAreNotMistakenForExecutables()
    {
        Assert.Null(Parse("--live", "app"));
        Assert.Null(Parse("--unknown", "app"));
        Assert.Equal(new[] { "--live" }, Assert.IsType<RunOptions>(Parse("--", "--live")).Command);
    }

    [Fact]
    public void ProcessIncludesAreRepeatableAndEnableProfiling()
    {
        RunOptions options = Assert.IsType<RunOptions>(Parse(
            "--include-process", "Sherlock.*.dll", "--include-process", "Worker?.exe", "--", "dotnet", "test"));

        Assert.Equal(new[] { "Sherlock.*.dll", "Worker?.exe" }, options.IncludeProcesses);
        Assert.Equal(new[] { "dotnet", "test" }, options.Command);
        Assert.True(options.NeedsProfiler);
    }

    [Fact]
    public void ProcessIncludesAfterDoubleDashBelongToTheTarget()
    {
        RunOptions options = Assert.IsType<RunOptions>(Parse("--", "app", "--include-process", "Sherlock.*.dll"));

        Assert.Empty(options.IncludeProcesses);
        Assert.Equal(new[] { "app", "--include-process", "Sherlock.*.dll" }, options.Command);
        Assert.False(options.NeedsProfiler);
    }

    [Theory]
    [InlineData("")]
    [InlineData("path/Worker.dll")]
    [InlineData("*.dll\n*.exe")]
    public void InvalidProcessIncludesAreRejected(string pattern)
    {
        Assert.Null(Parse("--include-process", pattern, "app"));
    }
}
