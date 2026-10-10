using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Sherlock.Core.Collection;
using Sherlock.Core.Tests.Common;
using Xunit;

namespace Sherlock.Core.Tests.Collection;

public sealed class RunTargetTests : IDisposable
{
    [Theory]
    [InlineData(new[] { "dotnet", "/apps/Orders.Api.dll", "--port", "80" }, "Orders.Api")]
    [InlineData(new[] { "/usr/local/share/dotnet/dotnet", "exec", "Orders.Api.dll" }, "Orders.Api")]
    [InlineData(new[] { "dotnet", "test" }, "dotnet")]
    [InlineData(new[] { "./Orders.Api" }, "Orders.Api")]
    [InlineData(new[] { "/bin/sh", "run.sh", "x.dll" }, "sh")]
    public void AppNameIsTheEntryAssemblyOfADotnetHost(string[] command, string expected)
    {
        Assert.Equal(expected, RunTarget.AppName(command));
    }

    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public async Task StartReturnsAUsableTarget()
    {
        var options = new RunOptions { Command = ["dotnet", "--version"], OutputDirectory = _tmp.Path };
        using RunTarget target = RunTarget.Start(options);

        int exitCode = await target.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.True(target.HasExited);
        Assert.Equal(0, target.ExitCode);
        Assert.True(target.Pid > 0);
        Assert.Equal(Path.GetFullPath(_tmp.Path), Path.GetFullPath(target.Options.OutputDirectory!));
        Assert.NotEmpty(target.ReadLog(10));
    }

    [Fact]
    public void StartRejectsAnEmptyCommand()
    {
        var options = new RunOptions { Command = [] };
        Assert.Throws<ArgumentException>(() => RunTarget.Start(options));
    }

    [Fact]
    public void SnapshotOnExitIsDetectedAmongOtherEvents()
    {
        var options = new RunOptions
        {
            Command = ["dotnet", "app.dll"],
            Correlate = true,
            SnapshotOn = "throw:Marker; exit",
        };

        Assert.True(options.SnapshotOnExit);
        Assert.False((options with { SnapshotOn = "throw:Marker" }).SnapshotOnExit);
    }

    // Metrics take any pid, so the tests read this test process; the launched command only provides a target.
    private RunTarget StartIdleTarget() =>
        RunTarget.Start(new RunOptions { Command = ["dotnet", "--version"], OutputDirectory = _tmp.Path });

    private static bool HasDiagnosticsPort(int pid) =>
        OperatingSystem.IsWindows() ||
        Directory.GetFiles(Path.GetTempPath(), $"dotnet-diagnostic-{pid}-*-socket").Length > 0;

    [Fact]
    public async Task MetricsStreamHeapStatsFromOneSessionPerProcess()
    {
        int pid = Environment.ProcessId;
        if (!HasDiagnosticsPort(pid))
        {
            Assert.Skip("The runtime could not open its diagnostics socket (TMPDIR may be too long).");
        }
        using RunTarget target = StartIdleTarget();

        Assert.Equal(new RuntimeMetrics(null, null), target.Metrics(pid)); // the session has just started
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        RuntimeMetrics metrics = target.Metrics(pid);
        while (metrics.Heap is null && elapsed.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            metrics = target.Metrics(pid);
        }

        Assert.Null(metrics.Error);
        Assert.True(metrics.Heap?.Total > 0);
    }

    [Fact]
    public void MetricsReportUnreachableProcessesWithoutThrowing()
    {
        using RunTarget target = StartIdleTarget();

        RuntimeMetrics metrics = target.Metrics(int.MaxValue);

        Assert.Null(metrics.Heap);
        Assert.False(string.IsNullOrWhiteSpace(metrics.Error));
        Assert.Equal(metrics, target.Metrics(int.MaxValue)); // remembered instead of retried on every read
    }

    [Fact]
    public async Task MetricsNeverStartASessionWhileACaptureHoldsTheTarget()
    {
        int pid = Environment.ProcessId;
        if (!HasDiagnosticsPort(pid))
        {
            Assert.Skip("The runtime could not open its diagnostics socket (TMPDIR may be too long).");
        }
        using RunTarget target = StartIdleTarget();

        target.DiagnosticsGate.Wait(TestContext.Current.CancellationToken);
        try
        {
            for (int i = 0; i < 15; i++)
            {
                Assert.Equal(new RuntimeMetrics(null, null), target.Metrics(pid));
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            target.DiagnosticsGate.Release();
        }

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (target.Metrics(pid).Heap is null && elapsed.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.NotNull(target.Metrics(pid).Heap);
    }

    [Fact]
    public async Task ProcessesIncludesAChildProcess()
    {
        string[] command = OperatingSystem.IsWindows()
            ? ["cmd.exe", "/d", "/c", "ping -n 6 127.0.0.1 >NUL"]
            : ["/bin/sh", "-c", "sleep 5 & wait"];
        using RunTarget target = RunTarget.Start(new RunOptions { Command = command, OutputDirectory = _tmp.Path });
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                if (target.Processes().Any(process => !process.IsRoot && process.ParentPid == target.Pid))
                {
                    return;
                }
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            Assert.Fail("The launched child process was not discovered.");
        }
        finally
        {
            target.Kill();
        }
    }
}
