using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// Blocks until an armed snapshot trigger fires (and its heap dump is captured), or a
/// timeout elapses. Makes scripts that arm a trigger and then inspect it deterministic —
/// no guessing a <c>sleep</c> long enough for the event.
/// </summary>
public sealed class WaitTriggerReplCommand : IReplCommand
{
    private const double DefaultTimeoutSeconds = 30;

    public string Name => "wait-trigger";
    public IReadOnlyList<string> Aliases => ["waitfor"];
    public string Summary => "Wait until an armed snapshot trigger fires (or times out).";
    public string Category => "Live";
    public string Usage => "wait-trigger [seconds]";

    public void Execute(ReplContext context, string[] args)
    {
        double timeout = DefaultTimeoutSeconds;
        if (args.Length > 0 && double.TryParse(args[0], out double t) && t > 0)
        {
            timeout = t;
        }

        bool anyLive = context.Workspace.Targets.Any(target => !target.RootExited);
        if (!anyLive)
        {
            context.Console.MarkupLine("[yellow]No live target to wait on.[/]");
            return;
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(timeout);
        context.Console.Status().Start("Waiting for a trigger to fire…", _ =>
        {
            while (DateTime.UtcNow < deadline)
            {
                IReadOnlyList<(SnapshotEntry Entry, string Probe)> caught = context.Workspace.HarvestProbeSnapshots();
                if (caught.Count > 0)
                {
                    foreach ((SnapshotEntry entry, string probe) in caught)
                    {
                        context.Console.MarkupLineInterpolated(
                            $"[green]●[/] [bold]{probe}[/] [green]fired — heap snapshot[/] [bold]{entry.Id}[/] [grey]captured.[/]");
                    }
                    return;
                }
                Thread.Sleep(150);
            }
            context.Console.MarkupLine("[yellow]timed out[/] [grey]waiting for a trigger.[/]");
        });
    }
}
