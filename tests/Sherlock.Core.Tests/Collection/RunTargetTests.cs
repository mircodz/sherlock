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
