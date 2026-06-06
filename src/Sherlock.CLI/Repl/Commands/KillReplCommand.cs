using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Collection;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// Snapshots a run target, then kills its process tree. Pass <c>--no-snapshot</c>
/// to just kill.
/// </summary>
public sealed class KillReplCommand : IReplCommand
{
    public string Name => "kill";
    public string Summary => "Snapshot then kill a run target (default: the latest).";
    public string Usage => "kill [pid] [--no-snapshot]";
    public string Category => "Live";

    public void Execute(ReplContext context, string[] args)
    {
        IReadOnlyList<ProcessSupervisor> targets = context.Workspace.Targets;
        if (targets.Count == 0)
        {
            context.Console.MarkupLine("[grey]No run targets.[/]");
            return;
        }

        bool snapshot = !args.Contains("--no-snapshot");
        string? pidArg = args.FirstOrDefault(a => !a.StartsWith('-'));

        ProcessSupervisor? target;
        if (pidArg is not null)
        {
            if (!int.TryParse(pidArg, out int pid))
            {
                context.Console.MarkupLineInterpolated($"[red]error:[/] '{pidArg}' is not a pid.");
                return;
            }
            target = targets.FirstOrDefault(t => t.RootPid == pid);
            if (target is null)
            {
                context.Console.MarkupLineInterpolated($"[red]error:[/] no run target with pid {pid}.");
                return;
            }
        }
        else
        {
            target = targets[^1];
        }

        // Snapshot while it's still alive, then kill.
        if (snapshot && !target.RootExited)
        {
            try
            {
                SnapshotEntry entry = context.Console.Status().Start($"Snapshotting pid {target.RootPid} before kill…",
                    _ => context.Workspace.Collect(target.RootPid, DumpKind.Heap, SnapshotOrigin.Run, load: false));
                context.Console.MarkupLineInterpolated(
                    $"[green]saved[/] [bold]{entry.Id}[/] [grey]({ByteSize.Format(entry.SizeBytes)}) — load {entry.Id} to analyze[/]");
            }
            catch (DumpAnalysisException ex)
            {
                context.Console.MarkupLineInterpolated($"[yellow]could not snapshot ({ex.Message}); killing anyway[/]");
            }
        }

        target.Kill();
        context.Console.MarkupLineInterpolated($"[grey]killed[/] {target.RootName} [grey](pid {target.RootPid})[/]");
    }
}
