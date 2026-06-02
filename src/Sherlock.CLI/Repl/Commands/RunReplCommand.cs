using Sherlock.Core;
using Sherlock.Core.Collection;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Launches a process under supervision; it runs in the background as a live target.</summary>
public sealed class RunReplCommand : IReplCommand
{
    public string Name => "run";
    public string Summary => "Launch a process and track it as a live target.";
    public string Usage => "run <path> [args...]";
    public string Category => "Live";

    public void Execute(ReplContext context, string[] args)
    {
        if (args.Length == 0)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage}");
            return;
        }

        var supervisor = new ProcessSupervisor();
        try
        {
            SupervisedProcess root = supervisor.Start(args[0], args[1..], dumpOnCrash: true);
            context.Workspace.AddTarget(supervisor);
            context.Console.MarkupLineInterpolated(
                $"[green]launched[/] {Path.GetFileName(args[0])} [grey](pid {root.Pid}). Use[/] ps[grey],[/] logs[grey],[/] snapshot[grey].[/]");
        }
        catch (DumpAnalysisException ex)
        {
            supervisor.Dispose();
            context.Console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
        }
    }
}
