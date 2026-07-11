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
        RunSpec? spec = RunLauncher.Parse(args, context.Console);
        if (spec is null)
        {
            return;
        }

        if (RunLauncher.Launch(context.Workspace, context.Console, spec) is not { } launched)
        {
            return;
        }

        Session session = launched.Session;
        if (spec.SnapshotOn is not null)
        {
            context.Console.MarkupLineInterpolated(
                $"[grey]snapshot-on[/] {spec.SnapshotOn}[grey] armed — a heap snapshot is captured into[/] [bold]{session.Id}[/][grey] when it fires.[/]");
        }
        else if (spec.Correlate)
        {
            context.Console.MarkupLine(
                "[grey]correlation tracking on;[/] snapshot [grey]it, then[/] whoalloc <address> [grey]to see where an object was allocated.[/]");
        }
        else if (spec.Profile)
        {
            context.Console.MarkupLineInterpolated($"[grey]allocation profiler attached; profile + log under[/] {session.Dir}[grey].[/]");
        }
        else
        {
            context.Console.MarkupLine("[grey]Use[/] ps[grey],[/] logs[grey],[/] snapshot[grey].[/]");
        }
    }
}
