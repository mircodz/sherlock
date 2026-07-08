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
    public string Summary => "Allocation views: call tree (default), hot methods, callers.";
    public string Category => "Allocation profiling";
    public string Usage => "allocations [tree|hot|callers <method>] [path] [count]";

    private static bool IsMode(string arg) => arg is "tree" or "hot" or "callers";

    public void Execute(ReplContext context, string[] args)
    {
        int limit = DefaultLimit;
        string? path = null;
        string mode = "tree";
        string? method = null;

        int i = 0;
        if (args.Length > 0 && IsMode(args[0]))
        {
            mode = args[0];
            i = 1;
            if (mode == "callers")
            {
                if (args.Length <= i)
                {
                    context.Console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage}");
                    return;
                }
                method = args[i++];
            }
        }
        for (; i < args.Length; i++)
        {
            if (int.TryParse(args[i], out int n))
            {
                limit = n;
            }
            else
            {
                path = args[i];
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

        switch (mode)
        {
            case "hot": RenderHot(context.Console, profile, limit); break;
            case "callers": RenderCallers(context.Console, profile, method!); break;
            default: RenderTree(context.Console, profile); break;
        }

        context.Console.MarkupLineInterpolated(
            $"[grey]{profile.Sites.Count:N0} call paths,[/] [bold green]{ByteSize.Format(profile.TotalAllocBytes)}[/] [grey]allocated,[/] [bold green]{ByteSize.Format(profile.TotalSurvivedBytes)}[/] [grey]survived first GC.[/]");
    }

    /// <summary>Top-down call tree: nodes carry inclusive allocated (+survived) bytes.</summary>
    private static void RenderTree(IAnsiConsole console, AllocationProfile profile)
    {
        AllocationTreeNode root = AllocationTreeNode.Build(profile);
        long total = root.AllocBytes == 0 ? 1 : root.AllocBytes;
        const double minFraction = 0.01; // hide branches under 1% of total

        var tree = new Tree("[bold gold1]Allocation call tree[/] [grey](inclusive bytes · % of total · survived)[/]")
        {
            Style = new Style(foreground: Color.Grey),
        };
        AddChildren(tree, root, total, minFraction);
        console.Write(tree);
    }

    /// <summary>Hot methods: bottom-up, self bytes (allocated directly by the method) first.</summary>
    private static void RenderHot(IAnsiConsole console, AllocationProfile profile, int limit)
    {
        var self = new Dictionary<string, (long Bytes, long Count)>();
        var inclusive = new Dictionary<string, long>();
        foreach (AllocationSite site in profile.Sites)
        {
            string leaf = site.Frames[^1];
            (long Bytes, long Count) cur = self.GetValueOrDefault(leaf);
            self[leaf] = (cur.Bytes + site.AllocBytes, cur.Count + site.AllocCount);
            foreach (string frame in site.Frames.Distinct()) // inclusive = passes through, once per stack
            {
                inclusive[frame] = inclusive.GetValueOrDefault(frame) + site.AllocBytes;
            }
        }

        var table = new Table().Border(TableBorder.Square).Expand();
        table.AddColumn(new TableColumn("[bold]Self[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Inclusive[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Count[/]").RightAligned());
        table.AddColumn("[bold]Method[/]");

        foreach ((string method, (long Bytes, long Count) val) in self.OrderByDescending(kv => kv.Value.Bytes).Take(limit))
        {
            table.AddRow(
                $"[bold green]{ByteSize.Format(val.Bytes)}[/]",
                $"[green]{ByteSize.Format(inclusive.GetValueOrDefault(method))}[/]",
                $"[grey]{Counts.Compact(val.Count)}×[/]",
                Markup.Escape(method));
        }

        console.Write(table);
    }

    /// <summary>Back-traces: the inverted caller tree of a method, weighted by bytes through it.</summary>
    private static void RenderCallers(IAnsiConsole console, AllocationProfile profile, string method)
    {
        AllocationTreeNode root = AllocationTreeNode.BuildCallers(profile, method);
        if (root.AllocBytes == 0)
        {
            console.MarkupLineInterpolated(
                $"[yellow]No allocations flow through[/] {method}[yellow].[/] [grey]Check the name with[/] allocations hot[grey].[/]");
            return;
        }

        long total = root.AllocBytes;
        var tree = new Tree(
            $"[bold gold1]{Markup.Escape(method)}[/] [grey]— callers ·[/] [bold green]{ByteSize.Format(total)}[/] [grey]allocated through it[/]")
        {
            Style = new Style(foreground: Color.Grey),
        };
        AddChildren(tree, root, total, 0.01);
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
                $"[bold green]{ByteSize.Format(child.AllocBytes)}[/] [grey]{pct:0.0}% · {Counts.Compact(child.AllocCount)}×[/]  " +
                $"{Markup.Escape(child.Frame)} [grey]· {survPct:0}% surv[/]");
            AddChildren(tn, child, total, minFraction);
        }

        int hiddenCount = kids.Count - shown.Count;
        if (hiddenCount > 0)
        {
            long hiddenBytes = kids.Skip(shown.Count).Sum(c => c.AllocBytes);
            parent.AddNode($"[grey]… {hiddenCount} smaller ({ByteSize.Format(hiddenBytes)})[/]");
        }
    }

}
