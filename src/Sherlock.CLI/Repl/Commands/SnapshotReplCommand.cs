using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Collection;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Dumps a live target into the library and loads it.</summary>
public sealed class SnapshotReplCommand : IReplCommand
{
    public string Name => "snapshot";
    public IReadOnlyList<string> Aliases => ["snap"];
    public string Summary => "Snapshot a live target (default: the latest run) into the library.";
    public string Usage => "snapshot [pid]";
    public string Category => "Live";

    public void Execute(ReplContext context, string[] args)
    {
        int pid;
        if (args.Length > 0)
        {
            if (!int.TryParse(args[0], out pid))
            {
                context.Console.MarkupLineInterpolated($"[red]error:[/] '{args[0]}' is not a pid.");
                return;
            }
        }
        else
        {
            pid = context.Workspace.Targets.Count > 0 ? context.Workspace.Targets[^1].RootPid : 0;
            if (pid == 0)
            {
                context.Console.MarkupLine("[red]error:[/] no live target. Launch one with [bold]run[/], or give a pid.");
                return;
            }
        }

        Collect(context, pid);
    }

    internal static void Collect(ReplContext context, int pid)
    {
        // For a correlation run, force a GC and emit the live-object → allocation-stack sidecar
        // *right before* the dump so their addresses line up (the dump itself is the runtime's
        // own in-process diagnostic-server dump — see Workspace.Collect / DumpCollector).
        ProcessSupervisor? correlated = context.Workspace.Targets
            .FirstOrDefault(t => t.RootPid == pid && t.HasCorrelation && !t.RootExited);
        string? sidecar = null;
        long gcAtEmit = -1;
        if (correlated is not null)
        {
            (string? path, long gc) = context.Console.Status().Start("Forcing GC + emitting allocation provenance…",
                _ => correlated.RequestCorrelationSnapshot(TimeSpan.FromSeconds(10)));
            sidecar = path;
            gcAtEmit = gc;
        }

        SnapshotEntry entry;
        try
        {
            entry = context.Console.Status().Start($"Snapshotting pid {pid}…",
                _ => context.Workspace.Collect(pid, DumpKind.Heap));
        }
        catch (DumpAnalysisException ex)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return;
        }

        // Persist the sidecar next to the dump so the snapshot carries its own provenance.
        bool withProvenance = false;
        if (sidecar is not null && File.Exists(sidecar))
        {
            try { File.Copy(sidecar, entry.Path + ".corr.tsv", overwrite: true); withProvenance = true; }
            catch { /* keep the dump even if the sidecar copy fails */ }
        }

        context.Console.MarkupLineInterpolated(
            $"[green]saved & loaded[/] [bold]{entry.Id}[/] [grey]({ByteSize.Format(entry.SizeBytes)})[/]");
        if (withProvenance)
        {
            // Drift check: if a GC ran between the emit and the dump, some objects moved and
            // the address join is stale. Detect it (we can't prevent it with an external dump).
            bool drifted = correlated is not null && gcAtEmit >= 0 &&
                           correlated.GcCount(TimeSpan.FromSeconds(3)) is long now && now >= 0 && now != gcAtEmit;
            if (drifted)
            {
                context.Console.MarkupLine("[yellow]⚠ a GC ran during capture[/] [grey]— some allocation provenance may be stale; re-snapshot for exact results.[/]");
            }
            else
            {
                context.Console.MarkupLine("[grey]Allocation provenance captured (exact); use[/] whoalloc <address> [grey]to see where an object was allocated.[/]");
            }
        }
    }
}
