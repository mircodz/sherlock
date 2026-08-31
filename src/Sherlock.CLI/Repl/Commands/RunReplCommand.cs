using Sherlock.CLI.Rendering;
using Sherlock.Core.Collection;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Launches a process under supervision; it runs in the background as a live target.</summary>
public sealed class RunReplCommand : IReplCommand
{
    public string Name => "run";
    public string Summary => "Launch a process and track it as a live target.";
    public string Usage => RunLauncher.Usage;
    public string Category => "Live";

    public void Execute(ReplContext context, string[] args)
    {
        RunOptions? options = RunLauncher.Parse(args, context.Console);
        if (options is null)
        {
            return;
        }

        if (RunLauncher.Launch(context.Workspace, context.Console, options) is not { } launched)
        {
            return;
        }

        Session session = launched.Session;
        if (options.SnapshotOn is not null)
        {
            Output.Info(context.Console, $"Snapshot trigger [bold]{options.SnapshotOn}[/] armed for [bold]{session.Id}[/].");
        }
        else if (options.Correlate)
        {
            Output.Info(context.Console, $"Correlation enabled · use [bold]snapshot[/], then [bold]whoalloc <address>[/].");
        }
        else if (options.Profile)
        {
            Output.Info(context.Console, $"Allocation profiler attached · artifacts in {session.Dir}.");
        }
        else
        {
            Output.Info(context.Console, $"Use [bold]ps[/], [bold]logs[/], or [bold]snapshot[/].");
        }
    }
}
