using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Cellar.Widgets.Charts;
using Cellar.Widgets.Charts.Trees;
using Sherlock.Core;
using Sherlock.Core.Profiling;
using Sherlock.Heap;
using static Sherlock.CLI.Tui.ViewFormatting;
using static System.FormattableString;

namespace Sherlock.CLI.Tui;

internal static class AllocationsView
{
    /// <summary>What the program allocated, by type.</summary>
    public static Widget ByType(AllocationProfile? profile, Action<NavigationTarget> navigate)
    {
        if (profile is null)
        {
            return MissingProfile("Allocations");
        }
        return View("Allocations", TypeTable(profile, name => new TypeTarget(name, TypeTab.Allocations), navigate), "Enter call tree");
    }

    /// <summary>Every allocation's call stack as a flame graph.</summary>
    public static Widget Flow(AllocationProfile? profile)
    {
        if (profile is null)
        {
            return MissingProfile("Call tree");
        }
        AllocationTreeNode root = AllocationTreeNode.Build(profile);
        var flame = new FlameGraph<AllocationTreeNode> { LabelSelector = node => node.Label, Root = ToFlame(root, "all allocations") };
        return FlamePanel(flame, root.AllocBytes);
    }

    /// <summary>The methods that allocate the most.</summary>
    public static Widget Hot(AllocationProfile? profile, Action<NavigationTarget> navigate)
    {
        if (profile is null)
        {
            return MissingProfile("Hot methods");
        }
        IReadOnlyList<AllocationMethodStat> methods = profile.HotMethods();
        string title = methods.Count > MaxHotMethods ? Invariant($"Hot methods \u00b7 top {MaxHotMethods} of {methods.Count:N0}") : "Hot methods";
        return View(title, HotTable(methods.Take(MaxHotMethods), profile.TotalAllocBytes, navigate), "Enter open method");
    }

    private const int MaxHotMethods = 200;

    public static Widget ForType(AllocationProfile? profile, string typeName, Action<NavigationTarget> navigate)
    {
        if (profile is null)
        {
            return MissingProfile("Call tree");
        }
        AllocationTreeNode root = AllocationTreeNode.Build(profile.OfType(typeName));
        return View($"Call tree \u00b7 {ByteFormat.Human(root.AllocBytes)}", CallTree(root.Children, root.AllocBytes, navigate),
            "\u2192\u2190 expand  \u00b7  Enter open method");
    }

    public static Widget ForMethod(AllocationProfile? profile, MethodFrame method, Action<NavigationTarget> navigate)
    {
        if (profile is null)
        {
            return MissingProfile("Method");
        }
        AllocationProfile through = profile.Through([method]);
        if (through.Sites.Count == 0)
        {
            return Message("Method", $"Nothing allocates through {method.Label}.");
        }

        StyledText summary = StyledText.Of(method.Label).Bold().Fg(Theme.Current.Accent)
            .Append($"   {ByteFormat.Human(through.TotalAllocBytes)} allocated").Fg(Theme.Current.Success)
            .Append($"   {ByteFormat.Human(through.TotalSurvivedBytes)} survived").Fg(Theme.Current.Foreground)
            .Append($"   {Count(through.Sites.Sum(s => s.AllocCount), "object")}").Fg(Theme.Current.Muted)
            .Append($"   \u00b7   {Count(through.Sites.Count, "call path")}").Fg(Theme.Current.Muted)
            .Append($"   \u00b7   {Count(through.ByType().Count, "type")}").Fg(Theme.Current.Muted);

        Tabs tabs = Views()
            .Add("Allocated types", () => View("Allocated types", TypeTable(through, name => new TypeTarget(name), navigate), "Enter instances"))
            .Add("Callers", () =>
            {
                AllocationTreeNode callers = AllocationTreeNode.BuildCallers(profile, [method]);
                return callers.Children.Count > 0
                    ? View("Callers", CallTree(callers.Children, callers.AllocBytes, navigate), "\u2192\u2190 expand  \u00b7  Enter open method")
                    : Message("Callers", "This is a top-level frame \u2014 no callers.");
            });
        return new Stack(Direction.Vertical)
            .Add(new Padding(new Label(summary), new Thickness(1, 0)), Constraint.Length(1))
            .Add(tabs, Constraint.Fill());
    }

    internal static Table<AllocationTypeStat> TypeTable(AllocationProfile profile, Func<string, NavigationTarget> target, Action<NavigationTarget> navigate)
    {
        long total = Math.Max(1, profile.TotalAllocBytes);
        return Table(profile.ByType(), row => navigate(target(row.TypeName)),
            NameColumn<AllocationTypeStat>("Type", row => Rendering.DisplayNames.Type(row.TypeName)),
            NamespaceColumn<AllocationTypeStat>(row => Rendering.DisplayNames.Namespace(row.TypeName)),
            BytesColumn<AllocationTypeStat>("Allocated", row => row.AllocBytes, Theme.Current.Success),
            PercentColumn<AllocationTypeStat>(row => 100.0 * row.AllocBytes / total),
            BytesColumn<AllocationTypeStat>("Survived GC", row => row.SurvivedBytes, Theme.Current.Foreground),
            CountColumn<AllocationTypeStat>("Count", row => row.AllocCount),
            CountColumn<AllocationTypeStat>("Sites", row => row.SiteCount, width: 6));
    }

    /// <summary>The methods that allocate, with each one's share of the <paramref name="total"/> bytes allocated.</summary>
    internal static Table<AllocationMethodStat> HotTable(IEnumerable<AllocationMethodStat> methods, long total, Action<NavigationTarget> navigate)
    {
        total = Math.Max(1, total);
        return Table(methods, row => navigate(new MethodTarget(row.Method)),
            NameColumn<AllocationMethodStat>("Method", row => row.Method.Label),
            NamespaceColumn<AllocationMethodStat>(row => Printable.Escape(row.Method.Namespace)),
            BytesColumn<AllocationMethodStat>("Self", row => row.SelfBytes, Theme.Current.Success),
            PercentColumn<AllocationMethodStat>(row => 100.0 * row.SelfBytes / total),
            BytesColumn<AllocationMethodStat>("Inclusive", row => row.InclusiveBytes, Theme.Current.Foreground),
            CountColumn<AllocationMethodStat>("Count", row => row.AllocCount));
    }

    private static Widget CallTree(IEnumerable<AllocationTreeNode> roots, long total, Action<NavigationTarget> navigate)
    {
        long denominator = Math.Max(1, total);
        var tree = new TreeView<AllocationTreeNode>
        {
            RenderLabel = node => StyledText.Of(node.Value.Frame!.Label).Fg(Theme.Current.Accent).Underline().Link(new MethodTarget(node.Value.Frame)),
            ShowHeader = true,
            ShowGuides = true,
            Striped = true,
            OnLinkClick = payload => navigate(NavigationTarget.FromLink(payload)),
            OnActivate = node => navigate(new MethodTarget(node.Value.Frame!)),
        };
        tree.Columns.Add(new TreeColumn<AllocationTreeNode>("Allocated", 11,
            node => new StyledText(ByteFormat.Column(node.Value.AllocBytes), new Style(Theme.Current.Success, Color.Default))));
        tree.Columns.Add(new TreeColumn<AllocationTreeNode>("%", 6, node =>
        {
            double percent = 100.0 * node.Value.AllocBytes / denominator;
            Color color = percent >= 10 ? Theme.Current.Success : Theme.Current.Muted;
            return new StyledText(percent.ToString("0.0", CultureInfo.InvariantCulture), new Style(color, Color.Default));
        }));
        tree.Columns.Add(new TreeColumn<AllocationTreeNode>("Survived GC", 11,
            node => new StyledText(ByteFormat.Column(node.Value.SurvivedBytes), new Style(Theme.Current.Foreground, Color.Default))));
        tree.Columns.Add(new TreeColumn<AllocationTreeNode>("Count", 10,
            node => new StyledText(node.Value.AllocCount.ToString("N0", CultureInfo.InvariantCulture), Theme.Current.MutedStyle)));
        foreach (AllocationTreeNode node in roots)
        {
            tree.AddRoot(node, parent => parent.Children).ExpandAll();
        }
        tree.MarkDirty();
        return tree;
    }

    private static FlameNode<AllocationTreeNode> ToFlame(AllocationTreeNode node, string? label = null)
    {
        var frame = new FlameNode<AllocationTreeNode>(node, label ?? node.Frame!.Label, Math.Max(1, node.AllocBytes));
        foreach (AllocationTreeNode child in node.Children)
        {
            frame.Add(ToFlame(child));
        }
        return frame;
    }

    private static Widget FlamePanel(FlameGraph<AllocationTreeNode> flame, long total)
    {
        var info = new Label(StyledText.Empty());
        void ShowInfo()
        {
            FlameNode<AllocationTreeNode>? selected = flame.SelectedFrame;
            StyledText text = IsZoomedOut(flame)
                ? StyledText.Empty()
                : StyledText.Of("zoomed into ").Fg(Theme.Current.Muted).Append(flame.ZoomedFrame!.Label).Fg(Theme.Current.Accent).Append("      ");
            if (selected is not null)
            {
                long bytes = (long)selected.Weight;
                text.Append(selected.Label).Fg(Theme.Current.Foreground)
                    .Append($"   {ByteFormat.Human(bytes)}").Fg(Theme.Current.Success)
                    .Append(Invariant($"   {100.0 * bytes / Math.Max(1, total):0.0}% of total")).Fg(Theme.Current.Muted);
                if (selected.Value.AllocCount > 0)
                {
                    text.Append(Invariant($"   \u00b7   {selected.Value.AllocCount:N0} objects")).Fg(Theme.Current.Muted);
                }
            }
            info.Content = text;
        }
        flame.OnSelect = _ => ShowInfo();
        flame.OnZoom = _ => ShowInfo();
        ShowInfo();
        return new Stack(Direction.Vertical)
            .Add(new Padding(info, new Thickness(1, 0)), Constraint.Length(1))
            .Add(View("Call tree", new Padding(flame, new Thickness(1, 0)), "\u2190\u2191\u2193\u2192 move  \u00b7  Enter zoom in  \u00b7  Esc zoom out"),
                Constraint.Fill());
    }

    private static bool IsZoomedOut(FlameGraph<AllocationTreeNode> flame) =>
        flame.ZoomedFrame is null || ReferenceEquals(flame.ZoomedFrame, flame.Root);

    public static Widget MissingProfile(string title) =>
        Message(title, "No allocation profile in this snapshot. Capture one with sl run --profile or --correlate.");
}
