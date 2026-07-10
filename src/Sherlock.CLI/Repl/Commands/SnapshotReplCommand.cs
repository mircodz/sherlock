using System;
using System.Collections.Generic;
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
    public string Summary => "Snapshot a live .NET process into the library (default: the live app; `snapshot <pid>` for a specific one).";
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
            // No pid: pick from the live .NET processes across all runs. Prefer a single app
            // *child* (the target under a launcher like `dotnet run`); fall back to the single
            // live process; otherwise the target is ambiguous, so make the user choose.
            List<SupervisedProcess> live = context.Workspace.Targets
                .SelectMany(t => t.List())
                .Where(p => p.IsDotnet)
                .ToList();
            List<SupervisedProcess> children = live.Where(p => !p.IsRoot).ToList();

            SupervisedProcess? pick =
                children.Count == 1 ? children[0] :
                live.Count == 1 ? live[0] :
                null;

            if (pick is null)
            {
                if (live.Count == 0)
                {
                    context.Console.MarkupLine("[red]error:[/] no live .NET target. Launch one with [bold]run[/], or give a pid.");
                }
                else
                {
                    context.Console.MarkupLine("[yellow]Multiple live .NET processes[/] — pick one with [bold]snapshot <pid>[/]:");
                    foreach (SupervisedProcess p in live)
                    {
                        context.Console.MarkupLineInterpolated($"  {p.Pid}  {(p.IsRoot ? "root" : "child")}  {p.Name}");
                    }
                }
                return;
            }
            pid = pick.Pid;
        }

        Collect(context, pid);
    }

    internal static void Collect(ReplContext context, int pid)
    {
        // Coherent capture: for a profiled/correlated target, flush the allocation profile and
        // emit the correlation sidecar at the same instant as the dump, and bundle all three into
        // the snapshot folder (see SnapshotStore.AddSnapshot / SnapshotEntry).
        // Find the run that owns this pid (its root OR a live descendant), so a child snapshot
        // under `dotnet run` still captures allocation state — routed to that child's profiler.
        ProcessSupervisor? target = context.Workspace.Targets
            .FirstOrDefault(t => !t.RootExited && (t.RootPid == pid || t.List().Any(p => p.Pid == pid)));
        bool correlated = target is { HasCorrelation: true };
        string? correlationSrc = null;
        string? allocationsSrc = null;
        long gcAtEmit = -1;
        if (target is not null && (correlated || target.ProfileOutPath is not null))
        {
            (correlationSrc, allocationsSrc, gcAtEmit) = context.Console.Status().Start(
                "Forcing GC + capturing allocation state…", _ =>
                {
                    string? corr = null;
                    long gc = -1;
                    if (correlated)
                    {
                        (corr, gc) = target.RequestCorrelationSnapshot(pid, TimeSpan.FromSeconds(10));
                    }
                    string? alloc = target.FlushAllocations(pid, TimeSpan.FromSeconds(10));
                    return (corr, alloc, gc);
                });
        }

        SnapshotEntry entry;
        try
        {
            entry = context.Console.Status().Start($"Snapshotting pid {pid}…",
                _ => context.Workspace.Collect(pid, DumpKind.Heap, allocations: allocationsSrc, correlation: correlationSrc));
        }
        catch (DumpAnalysisException ex)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return;
        }

        bool withProvenance = entry.CorrelationPath is not null;

        context.Console.MarkupLineInterpolated(
            $"[green]saved & loaded[/] [bold]{entry.Id}[/] [grey]({ByteSize.Format(entry.SizeBytes)})[/]");
        if (withProvenance)
        {
            // Drift check: if a GC ran between the emit and the dump, some objects moved and
            // the address join is stale. Detect it (we can't prevent it with an external dump).
            bool drifted = correlated && gcAtEmit >= 0 &&
                           target!.GcCount(pid, TimeSpan.FromSeconds(3)) is long now && now >= 0 && now != gcAtEmit;
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
