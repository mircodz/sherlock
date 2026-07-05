using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core.Collection;
using Sherlock.Core.Profiling;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// Shows the top allocation sites from a profile captured by <c>run --profile</c>
/// (or any folded profile file): allocated vs. survived bytes by method.
/// </summary>
public sealed class AllocationsReplCommand : IReplCommand
{
    private const int DefaultLimit = 25;

    public string Name => "allocations";
    public IReadOnlyList<string> Aliases => ["alloc"];
    public string Summary => "Show top allocation sites (allocated vs. survived) from a profile.";
    public string Category => "Allocation profiling";
    public string Usage => "allocations [path] [count]";

    public void Execute(ReplContext context, string[] args)
    {
        int limit = DefaultLimit;
        string? path = null;
        foreach (string arg in args)
        {
            if (int.TryParse(arg, out int n))
            {
                limit = n;
            }
            else
            {
                path = arg;
            }
        }

        // Default to the current session's profile, if it has one.
        path ??= context.Workspace.CurrentSession?.AllocationsPath;
        if (path is null)
        {
            context.Console.MarkupLine("[yellow]No allocation profile.[/] Pass a path, or load a snapshot from a [bold]run --profile[/] session.");
            return;
        }
        if (!File.Exists(path))
        {
            // The aggregate profile is only written at process exit — but if the target is
            // still running with a control channel, ask the profiler to flush it now.
            ProcessSupervisor? live = context.Workspace.Targets.FirstOrDefault(
                t => t.SessionId == context.Workspace.CurrentSession?.Id && !t.RootExited);
            if (live is not null)
            {
                string? flushed = context.Console.Status()
                    .Start("Flushing live allocation profile…", _ => live.FlushAllocations(TimeSpan.FromSeconds(10)));
                if (flushed is null)
                {
                    context.Console.MarkupLineInterpolated(
                        $"[yellow]Couldn't flush[/] — [bold]{live.RootName}[/] (pid {live.RootPid}) didn't answer (no/old profiler?). It's written at exit regardless.");
                    return;
                }
                path = flushed;
            }
            else
            {
                context.Console.MarkupLineInterpolated($"[red]error:[/] profile not found: {path}");
                return;
            }
        }

        AllocationProfile profile = AllocationProfileReader.Read(path);
        if (profile.Sites.Count == 0)
        {
            context.Console.MarkupLine("[yellow]Profile has no sites.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn(new TableColumn("[bold]Allocated[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Survived[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]%[/]").RightAligned());
        table.AddColumn("[bold]Method[/]");

        foreach (AllocationSite site in profile.Sites.OrderByDescending(s => s.AllocBytes).Take(limit))
        {
            double survivedPct = site.AllocBytes == 0 ? 0 : 100.0 * site.SurvivedBytes / site.AllocBytes;
            table.AddRow(
                $"{ByteSize.Format(site.AllocBytes)} [grey]({site.AllocCount:N0})[/]",
                $"{ByteSize.Format(site.SurvivedBytes)} [grey]({site.SurvivedCount:N0})[/]",
                $"{survivedPct:0}%",
                Markup.Escape(site.Method));
        }

        context.Console.Write(table);
        context.Console.MarkupLineInterpolated(
            $"[grey]{profile.Sites.Count:N0} sites,[/] [bold]{ByteSize.Format(profile.TotalAllocBytes)}[/] [grey]allocated, [/][bold]{ByteSize.Format(profile.TotalSurvivedBytes)}[/] [grey]survived first GC.[/]");
    }
}
