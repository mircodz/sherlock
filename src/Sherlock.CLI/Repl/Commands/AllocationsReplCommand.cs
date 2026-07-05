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
    public string Summary => "Allocation call tree (allocated vs. survived bytes). --flat for a leaf list.";
    public string Category => "Allocation profiling";
    public string Usage => "allocations [--flat] [path] [count]";

    public void Execute(ReplContext context, string[] args)
    {
        bool flat = false;
        int limit = DefaultLimit;
        string? path = null;
        foreach (string arg in args)
        {
            if (arg == "--flat")
            {
                flat = true;
            }
            else if (int.TryParse(arg, out int n))
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

        if (flat)
        {
            RenderFlat(context.Console, profile, limit);
        }
        else
        {
            RenderTree(context.Console, profile);
        }

        context.Console.MarkupLineInterpolated(
            $"[grey]{profile.Sites.Count:N0} call paths,[/] [bold]{ByteSize.Format(profile.TotalAllocBytes)}[/] [grey]allocated, [/][bold]{ByteSize.Format(profile.TotalSurvivedBytes)}[/] [grey]survived first GC.[/]");
    }

    /// <summary>Top-down call tree: nodes carry inclusive allocated (+survived) bytes.</summary>
    private static void RenderTree(IAnsiConsole console, AllocationProfile profile)
    {
        AllocationTreeNode root = AllocationTreeNode.Build(profile);
        long total = root.AllocBytes == 0 ? 1 : root.AllocBytes;
        const double minFraction = 0.01; // hide branches under 1% of total

        var tree = new Tree("[bold]allocations[/] [grey](inclusive bytes; % of total; survived %)[/]");
        AddChildren(tree, root, total, minFraction);
        console.Write(tree);
    }

    private static void AddChildren(IHasTreeNodes parent, AllocationTreeNode node, long total, double minFraction)
    {
        IReadOnlyList<AllocationTreeNode> kids = node.Children;
        var shown = kids.Where(c => (double)c.AllocBytes / total >= minFraction).ToList();

        foreach (AllocationTreeNode child in shown)
        {
            double pct = 100.0 * child.AllocBytes / total;
            double survPct = child.AllocBytes == 0 ? 0 : 100.0 * child.SurvivedBytes / child.AllocBytes;
            TreeNode tn = parent.AddNode(
                $"[bold]{ByteSize.Format(child.AllocBytes)}[/] [grey]{pct:0.0}%[/]  {Markup.Escape(child.Frame)} [grey]· {survPct:0}% surv[/]");
            AddChildren(tn, child, total, minFraction);
        }

        int hiddenCount = kids.Count - shown.Count;
        if (hiddenCount > 0)
        {
            long hiddenBytes = kids.Skip(shown.Count).Sum(c => c.AllocBytes);
            parent.AddNode($"[grey]… {hiddenCount} smaller ({ByteSize.Format(hiddenBytes)})[/]");
        }
    }

    /// <summary>The old flat view keyed by leaf method, biggest first.</summary>
    private static void RenderFlat(IAnsiConsole console, AllocationProfile profile, int limit)
    {
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn(new TableColumn("[bold]Allocated[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Survived[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]%[/]").RightAligned());
        table.AddColumn("[bold]Stack (root → leaf)[/]");

        foreach (AllocationSite site in profile.Sites.OrderByDescending(s => s.AllocBytes).Take(limit))
        {
            double survivedPct = site.AllocBytes == 0 ? 0 : 100.0 * site.SurvivedBytes / site.AllocBytes;
            table.AddRow(
                $"{ByteSize.Format(site.AllocBytes)} [grey]({site.AllocCount:N0})[/]",
                $"{ByteSize.Format(site.SurvivedBytes)} [grey]({site.SurvivedCount:N0})[/]",
                $"{survivedPct:0}%",
                Markup.Escape(string.Join(" → ", site.Frames)));
        }

        console.Write(table);
    }
}
