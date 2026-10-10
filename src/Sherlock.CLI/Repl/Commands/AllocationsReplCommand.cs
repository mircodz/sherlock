using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Collection;
using Sherlock.Core.Profiling;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Shows the top allocation sites from a <c>run --profile</c> capture: allocated vs survived bytes by method.</summary>
public sealed class AllocationsReplCommand : IReplCommand
{
    private const int DefaultLimit = 25;

    public string Name => "allocations";
    public IReadOnlyList<string> Aliases => ["alloc"];
    public string Summary => "Allocation views: call tree (default), hot methods, callers.";
    public string Category => "Allocation profiling";
    public string Usage => "allocations [tree|hot|callers <method>] [path] [count]";
    public int MaxArgs => 4;

    public ReplResult Execute(ReplContext context, string[] args)
    {
        int limit = DefaultLimit;
        string? path = null;
        string mode = "tree";
        string? method = null;

        int i = 0;
        if (args.Length > 0 && args[0] is ("tree" or "hot" or "callers"))
        {
            mode = args[0];
            i = 1;
            if (mode == "callers")
            {
                Args.Require(args, i + 1, Usage);
                method = args[i++];
            }
        }
        for (; i < args.Length; i++)
        {
            // A number is the row count, and must be a valid one; anything else is a profile path.
            if (int.TryParse(args[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            {
                limit = Args.Count(args, i, DefaultLimit, Usage);
            }
            else
            {
                path = args[i];
            }
        }

        // Prefer explicit path, then snapshot, session, and live target.
        RunTarget? runTarget = context.Workspace.Targets.LastOrDefault(t => t.AllocationPath is not null);
        path ??= context.Workspace.CurrentEntry?.ProvenancePath
               ?? context.Workspace.CurrentSession?.Processes.FirstOrDefault(p => p.HasAllocations)?.AllocationsPath
               ?? runTarget?.AllocationPath;
        if (path is null)
        {
            context.Console.MarkupLine($"[{Palette.Warning}]No allocation profile.[/] Pass a path, or run something with [bold]run --profile[/].");
            return ReplResult.Failure;
        }
        if (!File.Exists(path))
        {
            // Live profiles may need an explicit flush before their first read.
            RunTarget? live = context.Workspace.Targets.FirstOrDefault(
                t => !t.HasExited && t.AllocationPath == path)
                ?? context.Workspace.FindTarget(context.Workspace.CurrentSession);
            if (live is not null)
            {
                try
                {
                    path = context.Console.Status().Start("Flushing live allocation profile…", _ => live.CaptureAllocations(live.PrimaryPid, TimeSpan.FromSeconds(10)));
                }
                catch (DumpAnalysisException ex)
                {
                    context.Console.MarkupLineInterpolated($"[{Palette.Warning}]Couldn't flush[/] — {ex.Message}");
                    return ReplResult.Failure;
                }
            }
            else
            {
                Output.Error(context.Console, $"Profile not found: {path}");
                return ReplResult.Failure;
            }
        }

        AllocationProfile profile = AllocationProfileReader.Read(path);
        if (profile.Sites.Count == 0)
        {
            context.Console.MarkupLine($"[{Palette.Warning}]Profile has no sites.[/]");
            return ReplResult.Success;
        }

        switch (mode)
        {
            case "hot": RenderHot(context.Console, profile, limit); break;
            case "callers": RenderCallers(context.Console, profile, method!); break;
            default: RenderTree(context.Console, profile); break;
        }

        context.Console.MarkupLineInterpolated(
            $"[{Palette.Muted}]{Counts.Format(profile.Sites.Count)} call paths,[/] [bold {Palette.Hot}]{ByteSize.Format(profile.TotalAllocBytes)}[/] [{Palette.Muted}]allocated,[/] [bold {Palette.Text}]{ByteSize.Format(profile.TotalSurvivedBytes)}[/] [{Palette.Muted}]survived first GC.[/]");
        return ReplResult.Success;
    }

    private static void RenderTree(IAnsiConsole console, AllocationProfile profile)
    {
        AllocationTreeNode root = AllocationTreeNode.Build(profile);
        long total = root.AllocBytes == 0 ? 1 : root.AllocBytes;
        const double minFraction = 0.01; // hide branches under 1% of total

        var tree = new Tree($"[bold]Allocation call tree[/] [{Palette.Muted}](method · allocated · % total · objects · survived)[/]")
        {
            Style = new Style(foreground: Theme.MutedColor),
        };
        AddChildren(tree, root, total, minFraction);
        console.Write(tree);
    }

    private static void RenderHot(IAnsiConsole console, AllocationProfile profile, int limit)
    {
        var table = Theme.Table(expand: true);
        table.AddColumn(new TableColumn("[bold]Self[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Inclusive[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Count[/]").RightAligned());
        table.AddColumn("[bold]Method[/]");
        table.AddColumn("[bold]Namespace[/]");

        foreach (AllocationMethodStat method in profile.HotMethods(limit))
        {
            table.AddRow(
                $"[bold {Palette.Hot}]{ByteSize.Format(method.SelfBytes)}[/]",
                $"[{Palette.Text}]{ByteSize.Format(method.InclusiveBytes)}[/]",
                $"[{Palette.Muted}]{Counts.Compact(method.AllocCount)}×[/]",
                Styled.Method(method.Method),
                Styled.Namespace(FrameNames.Split(method.Method).Type));
        }

        console.Write(table);
    }

    private static void RenderCallers(IAnsiConsole console, AllocationProfile profile, string method)
    {
        AllocationTreeNode root = AllocationTreeNode.BuildCallers(profile, method);
        if (root.AllocBytes == 0)
        {
            console.MarkupLineInterpolated(
                $"[{Palette.Warning}]No allocations flow through[/] {method}[{Palette.Warning}].[/] [{Palette.Muted}]Check the name with[/] allocations hot[{Palette.Muted}].[/]");
            return;
        }

        long total = root.AllocBytes;
        var tree = new Tree(
            $"[{Palette.Name}]{Markup.Escape(FrameNames.ShortMethod(method))}[/] [{Palette.Muted}]— callers ·[/] [bold {Palette.Hot}]{ByteSize.Format(total)}[/] [{Palette.Muted}]allocated through it[/]")
        {
            Style = new Style(foreground: Theme.MutedColor),
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
                $"{Styled.Method(child.Frame)}  [bold {Palette.Hot}]{ByteSize.Format(child.AllocBytes)}[/] " +
                $"[{Palette.Muted}]· {Counts.Percent(pct)} · {Counts.Compact(child.AllocCount)}× · {Counts.Percent(survPct, 0)} surv[/]");
            AddChildren(tn, child, total, minFraction);
        }

        int hiddenCount = kids.Count - shown.Count;
        if (hiddenCount > 0)
        {
            long hiddenBytes = kids.Skip(shown.Count).Sum(c => c.AllocBytes);
            parent.AddNode($"[{Palette.Muted}]… {hiddenCount} smaller ({ByteSize.Format(hiddenBytes)})[/]");
        }
    }
}
