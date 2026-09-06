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
using static Sherlock.CLI.Tui.ViewFormatting;

namespace Sherlock.CLI.Tui;

internal static class AllocationsView
{
    public static Widget Create(AllocationProfile? profile, Action<NavigationTarget> navigate)
    {
        if (profile is null)
        {
            return MissingProfile(" Allocations ");
        }
        var tabs = new Tabs();
        if (profile.HasTypes)
        {
            tabs.Add("By type", () => Hinted(new Panel(TypeTable(profile, name => new TypeTarget(name, TypeTab.Allocations), navigate),
                " Allocations by type \u2014 what the program allocated ") { BorderStyle = BorderStyle.Rounded },
                "Enter \u2192 where this type came from  \u00b7  s sort  \u00b7  Backspace back"));
        }
        tabs.Add("Call tree", () =>
        {
            AllocationTreeNode root = AllocationTreeNode.Build(profile);
            var flame = new FlameGraph<AllocationTreeNode> { LabelSelector = node => node.Label, Root = ToFlame(root, "all allocations") };
            return FlamePanel(flame, root.AllocBytes);
        });
        tabs.Add("Hot methods", () => Hinted(new Panel(HotTable(profile, navigate), " Hot allocation sites ") { BorderStyle = BorderStyle.Rounded },
            "Enter \u2192 callers \u00b7 s sort \u00b7 Backspace back"));
        return tabs;
    }

    public static Widget ForType(AllocationProfile? profile, string typeName, Action<NavigationTarget> navigate)
    {
        if (profile is null)
        {
            return MissingProfile(" Call tree ");
        }
        AllocationTreeNode root = AllocationTreeNode.Build(profile.OfType(typeName));
        return Hinted(new Panel(CallTree(root.Children, root.AllocBytes, navigate),
            $" Where {TypeNames.Short(typeName)} came from \u2014 {ByteFormat.Human(root.AllocBytes)} ") { BorderStyle = BorderStyle.Rounded },
            "\u2192/\u2190 expand \u00b7 collapse   \u00b7   click a frame \u2192 its callers   \u00b7   Backspace back");
    }

    public static Widget ForMethod(AllocationProfile? profile, string method, Action<NavigationTarget> navigate)
    {
        if (profile is null)
        {
            return Hinted(MissingProfile(" Method "), "Backspace back");
        }
        AllocationProfile through = profile.Through(method);
        if (through.Sites.Count == 0)
        {
            return Hinted(new Panel(new Padding(new Label(new StyledText($"Nothing allocates through {TypeNames.Short(method)}.", Theme.Current.MutedStyle)),
                new Thickness(1)), " Method ") { BorderStyle = BorderStyle.Rounded }, "Backspace back");
        }

        StyledText summary = StyledText.Of(TypeNames.Short(method)).Bold().Fg(Theme.Current.Accent)
            .Append($"   {ByteFormat.Human(through.TotalAllocBytes)} allocated").Fg(Theme.Current.Success)
            .Append($"   {ByteFormat.Human(through.TotalSurvivedBytes)} survived").Fg(Theme.Current.Foreground)
            .Append($"   {through.Sites.Sum(s => s.AllocCount):N0} objects").Fg(Theme.Current.Muted)
            .Append($"   \u00b7   {through.Sites.Count:N0} call paths").Fg(Theme.Current.Muted);
        if (through.HasTypes)
        {
            summary.Append($"   \u00b7   {through.ByType().Count} types").Fg(Theme.Current.Muted);
        }

        var tabs = new Tabs();
        if (through.HasTypes)
        {
            tabs.Add("Allocated types", () => Hinted(new Panel(TypeTable(through, name => new TypeTarget(name), navigate),
                " What this method allocates, by type ") { BorderStyle = BorderStyle.Rounded },
                "Enter \u2192 live instances of this type  \u00b7  s sort  \u00b7  Backspace back"));
        }
        tabs.Add("Callers", () =>
        {
            AllocationTreeNode callers = AllocationTreeNode.BuildCallers(profile, method);
            return callers.Children.Count > 0
                ? Hinted(new Panel(CallTree(callers.Children, callers.AllocBytes, navigate),
                    " Who calls it \u2014 bytes allocated through each caller ") { BorderStyle = BorderStyle.Rounded },
                    "\u2192/\u2190 expand \u00b7 collapse   \u00b7   click a frame \u2192 its stats   \u00b7   Backspace back")
                : new Panel(new Padding(new Label(new StyledText("This is a top-level frame \u2014 no callers.", Theme.Current.MutedStyle)),
                    new Thickness(1)), " Callers ") { BorderStyle = BorderStyle.Rounded };
        });
        return new Stack(Direction.Vertical)
            .Add(new Padding(new Label(summary), new Thickness(1, 0)), Constraint.Length(1))
            .Add(tabs, Constraint.Fill());
    }

    internal static Table TypeTable(AllocationProfile profile, Func<string, NavigationTarget> target, Action<NavigationTarget> navigate)
    {
        long total = Math.Max(1, profile.TotalAllocBytes);
        Table table = Table(("Type", Constraint.Fill(3), false), ("Allocated", Constraint.Length(11), true),
            ("Survived", Constraint.Length(11), true), ("Count", Constraint.Length(10), true),
            ("Sites", Constraint.Length(6), true), ("share", Constraint.Length(12), false));
        SetRows(table, profile.ByType(), row =>
            [row.TypeName, ByteFormat.Human(row.AllocBytes), ByteFormat.Human(row.SurvivedBytes),
                row.AllocCount.ToString("N0", CultureInfo.InvariantCulture), row.SiteCount.ToString(CultureInfo.InvariantCulture),
                Bar(100.0 * row.AllocBytes / total, 10)],
            row => navigate(target(row.TypeName)));
        return table;
    }

    internal static Table HotTable(AllocationProfile profile, Action<NavigationTarget> navigate)
    {
        Table table = Table(("Self", Constraint.Length(11), true), ("Inclusive", Constraint.Length(11), true),
            ("Count", Constraint.Length(9), true), ("Method", Constraint.Fill(3), false));
        SetRows(table, profile.HotMethods(200), row =>
            [ByteFormat.Human(row.SelfBytes), ByteFormat.Human(row.InclusiveBytes),
                row.AllocCount.ToString("N0", CultureInfo.InvariantCulture), TypeNames.Short(row.Method)],
            row => navigate(new MethodTarget(row.Method)));
        return table;
    }

    private static Widget CallTree(IEnumerable<AllocationTreeNode> roots, long total, Action<NavigationTarget> navigate)
    {
        long denominator = Math.Max(1, total);
        var tree = new TreeView<AllocationTreeNode>
        {
            RenderLabel = node => StyledText.Of(TypeNames.Short(node.Value.Frame)).Fg(Theme.Current.Accent).Underline().Link(new MethodTarget(node.Value.Frame)),
            ShowHeader = true,
            ShowGuides = true,
            Striped = true,
            OnLinkClick = payload => navigate(NavigationTarget.FromLink(payload)),
            OnActivate = node => navigate(new MethodTarget(node.Value.Frame)),
        };
        tree.Columns.Add(new TreeColumn<AllocationTreeNode>("Allocated", 11,
            node => new StyledText(ByteFormat.Human(node.Value.AllocBytes), new Style(Theme.Current.Success, Color.Default))));
        tree.Columns.Add(new TreeColumn<AllocationTreeNode>("%", 6, node =>
        {
            double percent = 100.0 * node.Value.AllocBytes / denominator;
            Color color = percent >= 10 ? Theme.Current.Success : Theme.Current.Muted;
            return new StyledText(percent.ToString("0.0", CultureInfo.InvariantCulture), new Style(color, Color.Default));
        }));
        tree.Columns.Add(new TreeColumn<AllocationTreeNode>("Survived", 11,
            node => new StyledText(ByteFormat.Human(node.Value.SurvivedBytes), new Style(Theme.Current.Foreground, Color.Default))));
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
        var frame = new FlameNode<AllocationTreeNode>(node, label ?? TypeNames.Short(node.Frame), Math.Max(1, node.AllocBytes));
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
            StyledText text = StyledText.Of("zoom ").Fg(Theme.Current.Muted)
                .Append(flame.ZoomedFrame?.Label ?? "all").Fg(Theme.Current.Accent);
            if (selected is not null)
            {
                long bytes = (long)selected.Weight;
                text.Append("      ").Append(selected.Label).Fg(Theme.Current.Foreground)
                    .Append($"   {ByteFormat.Human(bytes)}").Fg(Theme.Current.Success)
                    .Append($"   {100.0 * bytes / Math.Max(1, total):0.0}% of total").Fg(Theme.Current.Muted);
                if (selected.Value.AllocCount > 0)
                {
                    text.Append($"   \u00b7   {selected.Value.AllocCount:N0} objects").Fg(Theme.Current.Muted);
                }
            }
            info.Content = text;
        }
        flame.OnSelect = _ => ShowInfo();
        flame.OnZoom = _ => ShowInfo();
        ShowInfo();
        return new Stack(Direction.Vertical)
            .Add(new Padding(info, new Thickness(1, 0)), Constraint.Length(1))
            .Add(Hinted(new Panel(new Padding(flame, new Thickness(1, 0)), " Allocation flow \u2014 width = bytes ") { BorderStyle = BorderStyle.Rounded },
                "click a frame to zoom \u00b7 Backspace zooms out \u00b7 \u2191\u2193\u2190\u2192 navigate"), Constraint.Fill());
    }

    private static Widget MissingProfile(string title) =>
        new Panel(new Padding(new Label(new StyledText(
            "No allocation profile in this snapshot. Capture with `run --profile` or `run --correlate`.", Theme.Current.MutedStyle)),
            new Thickness(1)), title) { BorderStyle = BorderStyle.Rounded };
}
