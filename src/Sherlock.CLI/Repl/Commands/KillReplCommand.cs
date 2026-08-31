using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Collection;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Snapshots a run target, then kills its process tree. <c>--no-snapshot</c> to just kill.</summary>
public sealed class KillReplCommand : IReplCommand
{
    public string Name => "kill";
    public string Summary => "Snapshot then kill a run target (default: the latest).";
    public string Usage => "kill [pid] [--no-snapshot]";
    public string Category => "Live";

    public void Execute(ReplContext context, string[] args)
    {
        IReadOnlyList<RunTarget> targets = context.Workspace.Targets;
        if (targets.Count == 0)
        {
            Output.Info(context.Console, $"No run targets.");
            return;
        }

        bool snapshot = !args.Contains("--no-snapshot");
        string? pidArg = args.FirstOrDefault(a => !a.StartsWith('-'));

        RunTarget? target;
        if (pidArg is not null)
        {
            if (!int.TryParse(pidArg, out int pid))
            {
                Output.Error(context.Console, $"'{pidArg}' is not a pid.");
                return;
            }
            target = targets.FirstOrDefault(t => t.Pid == pid);
            if (target is null)
            {
                Output.Error(context.Console, $"No run target with pid {pid}.");
                return;
            }
        }
        else
        {
            target = targets[^1];
        }

        // Snapshot while it's still alive, then kill.
        if (snapshot && !target.HasExited)
        {
            try
            {
                SnapshotEntry entry = context.Console.Status().Start(
                    $"Snapshotting pid {target.Pid} before kill…",
                    _ => context.Workspace.Capture(target.Pid, load: false).Entry);
                string contents = entry.HasAllocations ? "heap + allocations" : "heap only";
                Output.Success(context.Console, $"Saved [bold]{entry.Id}[/] [#808791]({contents} · {ByteSize.Format(entry.TotalSizeBytes)})[/]");
            }
            catch (DumpAnalysisException ex)
            {
                Output.Warning(context.Console, $"Could not snapshot: {ex.Message}. Killing anyway.");
            }
        }

        target.Kill();
        Output.Success(context.Console, $"Killed [#00D7FF]{target.Name}[/] · pid {target.Pid}");
    }
}
