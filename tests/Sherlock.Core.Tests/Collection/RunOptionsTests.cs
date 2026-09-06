using System;
using Sherlock.Core.Collection;
using Xunit;

namespace Sherlock.Core.Tests.Collection;

public sealed class RunOptionsTests
{
    [Fact]
    public void RejectsAnEmptyCommand()
    {
        Assert.Throws<ArgumentException>(() => new RunOptions { Command = [] }.Validate());
        Assert.Throws<ArgumentException>(() => new RunOptions { Command = null! }.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsABlankExecutable(string executable)
    {
        Assert.Throws<ArgumentException>(() => new RunOptions { Command = [executable] }.Validate());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(99)]
    public void RejectsUndefinedProfilerLogLevels(int level)
    {
        var options = new RunOptions { Command = ["app"], ProfilerLogLevel = (ProfilerLogLevel)level };
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void ExperimentalBarrierRequiresCorrelation()
    {
        var options = new RunOptions { Command = ["app"], ExperimentalGcBarrier = true };
        Assert.Throws<ArgumentException>(() => options.Validate());
        (options with { Correlate = true }).Validate();
        Assert.True((options with { Correlate = true }).UseGcBarrier);
    }

    [Fact]
    public void ValidOptionsPreserveCapturePolicyAndLiteralArguments()
    {
        var options = new RunOptions
        {
            Command = ["app", "", "--profile", "--"],
            Correlate = true,
            SnapshotOn = "throw:Marker; exit",
            ProfilerLogLevel = ProfilerLogLevel.Off,
        };
        options.Validate();
        Assert.True(options.NeedsProfiler);
        Assert.True(options.SnapshotOnExit);
        Assert.True(options.UseGcBarrier);
        Assert.False(options.ExperimentalGcBarrier);
        Assert.Equal(new[] { "app", "", "--profile", "--" }, options.Command);
    }

    [Fact]
    public void ProcessIncludesEnableProfilingWithoutChangingCorrelation()
    {
        var options = new RunOptions { Command = ["dotnet", "test"], IncludeProcesses = ["Sherlock.*.dll", "Worker?.exe"] };

        options.Validate();

        Assert.True(options.HasProcessFilter);
        Assert.True(options.NeedsProfiler);
        Assert.False(options.Correlate);
        Assert.False(options.UseGcBarrier);
        Assert.False(new RunOptions { Command = ["dotnet", "test"] }.NeedsProfiler);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("path/Sherlock.*.dll")]
    [InlineData("path\\Sherlock.*.dll")]
    [InlineData("Sherlock.*.dll\nOther.dll")]
    [InlineData("Sherlock.\t.dll")]
    [InlineData(null)]
    public void RejectsInvalidProcessFilenameGlobs(string? pattern)
    {
        var options = new RunOptions { Command = ["app"], IncludeProcesses = [pattern!] };
        Assert.Throws<ArgumentException>(() => options.Validate());
    }
}
