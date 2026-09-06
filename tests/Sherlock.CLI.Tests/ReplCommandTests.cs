using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Sherlock.CLI.Commands;
using Sherlock.CLI.Repl;
using Sherlock.CLI.Repl.Commands;
using Sherlock.Core;
using Sherlock.Core.Collection;
using Sherlock.Core.Store;
using Spectre.Console;
using Xunit;
using CommandRepl = Sherlock.CLI.Repl.Repl;

namespace Sherlock.CLI.Tests;

public sealed class ReplCommandTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(".test-artifacts", $"commands-{Guid.NewGuid():N}"));
    private readonly StringWriter _output = new();
    private readonly IAnsiConsole _console;
    private readonly Workspace _workspace;

    public ReplCommandTests()
    {
        _workspace = new Workspace(new SnapshotStore(Path.Combine(_root, "store")));
        _console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(_output),
            Ansi = AnsiSupport.No,
            Interactive = InteractionSupport.No,
        });
    }

    public void Dispose()
    {
        _workspace.Dispose();
        _output.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private CommandRepl DefaultRepl()
    {
        var history = new ReplHistory(null);
        return new CommandRepl(ReplCommandRegistry.CreateDefault(history), history, _console);
    }

    private CommandRepl ReplWith(ProbeCommand probe, ReplHistory? history = null) =>
        new(new ReplCommandRegistry([probe, new SourceReplCommand(), new SleepReplCommand()]), history ?? new ReplHistory(null), _console);

    private string Script(string name, params string[] lines)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllLines(path, lines);
        return $"source \"{path}\"";
    }

    [Theory]
    [InlineData("unknown-command")]
    [InlineData("help unknown-command")]
    [InlineData("info")]
    [InlineData("load missing")]
    [InlineData("import missing.dmp")]
    [InlineData("label missing label")]
    [InlineData("rm missing")]
    [InlineData("run")]
    [InlineData("run --snapshot-on")]
    [InlineData("run --snapshot-on exit app")]
    [InlineData("run --experimental-gc-barrier app")]
    [InlineData("run --live app")]
    [InlineData("snapshot")]
    [InlineData("snapshot --pid")]
    [InlineData("snapshot-on")]
    [InlineData("snapshot-on gc")]
    [InlineData("wait-trigger")]
    [InlineData("kill")]
    [InlineData("logs")]
    [InlineData("threads invalid")]
    [InlineData("allocations")]
    [InlineData("allocations callers")]
    [InlineData("source missing.sl")]
    [InlineData("sleep NaN")]
    [InlineData("sleep Infinity")]
    [InlineData("sleep 2147484")]
    [InlineData("quit unexpected")]
    [InlineData("help \"unterminated")]
    public void FailedCommandsHaveAnExplicitFailedOutcome(string line)
    {
        Assert.Equal(ReplResult.Failure, DefaultRepl().RunBatch(_workspace, [line]));
        Assert.Equal(1, AnalyzeCommand.Run(_console, _workspace, new AnalyzeCommand.Settings { Exec = [line] }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("help")]
    [InlineData("ls")]
    [InlineData("ps")]
    [InlineData("sleep 0")]
    public void SuccessfulEmptyResultsRemainSuccessful(string line)
    {
        Assert.Equal(ReplResult.Success, DefaultRepl().RunBatch(_workspace, [line]));
        Assert.Equal(0, AnalyzeCommand.Run(_console, _workspace, new AnalyzeCommand.Settings { Exec = [line] }));
    }

    [Fact]
    public void BatchContinuesAfterFailuresWithoutLosingThem()
    {
        var probe = new ProbeCommand((_, args) => args.Length == 0 ? ReplResult.Success : ReplResult.Failure);
        ReplResult result = ReplWith(probe).RunBatch(_workspace, ["unknown", "probe failure", "PROBE-ALIAS"]);
        Assert.Equal(ReplResult.Failure, result);
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public void ExceptionsAreFailuresRatherThanSuccessfulCommands()
    {
        var probe = new ProbeCommand((_, args) => args[0] == "expected"
            ? throw new DumpAnalysisException("expected error")
            : throw new InvalidOperationException("unexpected error"));
        Assert.Equal(ReplResult.Failure, ReplWith(probe).RunBatch(_workspace, ["probe expected", "probe unexpected"]));
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public void InvalidRunOptionsAreRejectedBeforeCreatingASession()
    {
        var options = new RunOptions { Command = ["app"], ExperimentalGcBarrier = true };
        Assert.Throws<ArgumentException>(() => RunLauncher.Launch(_workspace, _console, options));
        Assert.Empty(_workspace.Store.Sessions);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace.Store.Root));
    }

    [Fact]
    public void AFailedLaunchIsNotASuccessfulRunCommand()
    {
        string line = $"run \"{Path.Combine(_root, "missing-executable")}\"";
        Assert.Equal(ReplResult.Failure, DefaultRepl().RunBatch(_workspace, [line]));
        Assert.Empty(_workspace.Targets);
        Assert.Empty(_workspace.Store.Sessions);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("QUIT")]
    [InlineData("q")]
    public void QuitStopsExecutionButDoesNotErasePreviousFailure(string quit)
    {
        var probe = new ProbeCommand();
        Assert.Equal(ReplResult.Quit, ReplWith(probe).RunBatch(_workspace, [quit, "probe"]));
        Assert.Equal(ReplResult.Failure | ReplResult.Quit, ReplWith(probe).RunBatch(_workspace, ["unknown", quit, "probe"]));
        Assert.Equal(0, probe.Calls);
        Assert.Equal(0, AnalyzeCommand.Run(_console, _workspace, new AnalyzeCommand.Settings { Exec = [quit, "unknown"] }));
        Assert.Equal(1, AnalyzeCommand.Run(_console, _workspace, new AnalyzeCommand.Settings { Exec = ["unknown", quit] }));
    }

    [Fact]
    public void NestedSourcesContinueAfterErrorsAndReportFailure()
    {
        string inner = Script("inner.sl", "# ignored", "", "unknown", "source missing.sl", "probe");
        string outer = Script("outer.sl", inner, "probe");
        var probe = new ProbeCommand();
        Assert.Equal(ReplResult.Failure, ReplWith(probe).RunBatch(_workspace, [outer, "probe"]));
        Assert.Equal(3, probe.Calls);
    }

    [Fact]
    public void NestedQuitPropagatesWithAnyEarlierFailure()
    {
        string inner = Script("inner.sl", "unknown", "quit", "probe");
        string outer = Script("outer.sl", inner, "probe");
        var probe = new ProbeCommand();
        Assert.Equal(ReplResult.Failure | ReplResult.Quit, ReplWith(probe).RunBatch(_workspace, [outer, "probe"]));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void SourceCyclesFailAndLeaveTheReplUsable()
    {
        string path = Path.Combine(_root, "recursive.sl");
        string source = Script("recursive.sl", $"source \"{path}\"", "probe");
        var probe = new ProbeCommand();
        Assert.Equal(ReplResult.Failure, ReplWith(probe).RunBatch(_workspace, [source, source, "probe"]));
        Assert.Equal(3, probe.Calls);
    }

    [Fact]
    public void ScriptFailuresReachTheAnalyzeExitCode()
    {
        string source = Script("inner.sl", "missing-command");
        Script("outer.sl", "# comment", "", source, "help");
        Assert.Equal(1, AnalyzeCommand.Run(_console, _workspace, new AnalyzeCommand.Settings { Script = Path.Combine(_root, "outer.sl") }));
        Assert.Equal(1, AnalyzeCommand.Run(_console, _workspace, new AnalyzeCommand.Settings { Script = Path.Combine(_root, "absent.sl") }));
    }

    [Fact]
    public void SuccessfulScriptsAndQuitHaveSuccessfulExitCodes()
    {
        string source = Script("inner.sl", "sleep 0", "quit", "missing-command");
        Script("outer.sl", "# comment", "", source, "missing-command");
        Assert.Equal(0, AnalyzeCommand.Run(_console, _workspace, new AnalyzeCommand.Settings { Script = Path.Combine(_root, "outer.sl") }));
    }

    [Fact]
    public void ScriptReadFailuresAreNotSuccessfulBatches()
    {
        var probe = new ProbeCommand();
        Assert.Equal(ReplResult.Failure, ReplWith(probe).RunBatch(_workspace, Lines()));
        Assert.Equal(1, probe.Calls);

        static IEnumerable<string> Lines()
        {
            yield return "probe";
            throw new IOException("script read failed");
        }
    }

    [Fact]
    public void CancellationIsDistinctAndStopsABatch()
    {
        var probe = new ProbeCommand((_, _) => throw new OperationCanceledException());
        Assert.Equal(ReplResult.Cancelled, ReplWith(probe).RunBatch(_workspace, ["probe", "probe"]));
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public void NestedCancellationRetainsEarlierFailure()
    {
        var probe = new ProbeCommand((_, _) => throw new OperationCanceledException());
        string inner = Script("inner.sl", "probe", "probe");
        string outer = Script("outer.sl", "unknown", inner, "probe");
        Assert.Equal(ReplResult.Failure | ReplResult.Cancelled, ReplWith(probe).RunBatch(_workspace, [outer, "probe"]));
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public void EntryPointCancellationPreventsDispatch()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var probe = new ProbeCommand();
        Assert.Equal(ReplResult.Cancelled, ReplWith(probe).RunBatch(_workspace, ["probe"], cancellation.Token));
        Assert.Equal(0, probe.Calls);
        Assert.Equal(1, AnalyzeCommand.Run(_console, _workspace, new AnalyzeCommand.Settings { Exec = ["help"] }, cancellation.Token));
    }

    [Fact]
    public void CancellationDuringSleepReachesAnalyze()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        Assert.Equal(1, AnalyzeCommand.Run(_console, _workspace, new AnalyzeCommand.Settings { Exec = ["sleep 60"] }, cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public void ContextExposesCancellationAndDoesNotLoseItOnReturn()
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new ProbeCommand((context, _) =>
        {
            Assert.Equal(cancellation.Token, context.Cancellation);
            cancellation.Cancel();
            return ReplResult.Success;
        });
        Assert.Equal(ReplResult.Cancelled, ReplWith(probe).RunBatch(_workspace, ["probe", "probe"], cancellation.Token));
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public void InteractiveFailuresAndCommandCancellationRecoverAndKeepHistory()
    {
        var history = new ReplHistory(null);
        var probe = new ProbeCommand((_, args) => args.Length > 0 ? throw new OperationCanceledException() : ReplResult.Success);
        var lines = new Queue<string?>(["unknown", "probe cancel", "probe", "", "quit"]);
        ReplResult result = ReplWith(probe, history).RunInteractive(_workspace, _ => lines.Dequeue());
        Assert.Equal(ReplResult.Failure | ReplResult.Cancelled | ReplResult.Quit, result);
        Assert.Equal(3, probe.Calls);
        Assert.Equal(new[] { "unknown", "probe cancel", "probe", "quit" }, history.Entries);
    }

    [Fact]
    public void CancelledInputDoesNotRepeatThePreviousCommand()
    {
        int reads = 0;
        var probe = new ProbeCommand();
        ReplResult result = ReplWith(probe).RunInteractive(_workspace, _ => ++reads switch
        {
            1 => "probe",
            2 => throw new OperationCanceledException(),
            _ => null,
        });
        Assert.Equal(ReplResult.Cancelled | ReplResult.Quit, result);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public void TokenizerPreservesQuotedEmptyTargetArguments()
    {
        var probe = new ProbeCommand((_, args) =>
        {
            Assert.Equal(new[] { "", "two words", "--" }, args);
            return ReplResult.Success;
        });
        Assert.Equal(ReplResult.Success, ReplWith(probe).RunBatch(_workspace, ["probe \"\" \"two words\" --"]));
        Assert.Equal(1, probe.Calls);
    }

    private sealed class ProbeCommand(Func<ReplContext, string[], ReplResult>? execute = null) : IReplCommand
    {
        public string Name => "probe";
        public IReadOnlyList<string> Aliases => ["probe-alias"];
        public string Summary => "Probe command outcomes.";
        public string Usage => "probe";
        public int Calls { get; private set; }

        public ReplResult Execute(ReplContext context, string[] args)
        {
            Calls++;
            return execute?.Invoke(context, args) ?? ReplResult.Success;
        }
    }
}
