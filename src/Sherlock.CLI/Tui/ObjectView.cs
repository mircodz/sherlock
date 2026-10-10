using System;
using System.Collections.Generic;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Cellar.Widgets.Charts.Trees;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Sherlock.Core.Profiling;
using static Sherlock.CLI.Tui.ViewFormatting;

namespace Sherlock.CLI.Tui;

internal static class ObjectView
{
    public static Widget Create(Snapshot snapshot, ulong address, ObjectTab tab, Action<NavigationTarget> navigate)
    {
        ObjectValue value = snapshot.InspectValue(address);
        Tabs tabs = new Tabs()
            .AddView("Inspect", () => new ObjectInspectorView(value, snapshot.InspectChildren,
                target => navigate(new ObjTarget(target)), type => navigate(new TypeTarget(type))))
            .AddBackgroundView("GC roots", cancellation => Roots(snapshot.Roots(address, cancellation), navigate));

        if (snapshot.HasCorrelation && snapshot.WhoAllocated(address) is { } frames)
        {
            tabs.AddView("whoalloc", () => AllocationStack(frames, navigate));
        }
        tabs.ActiveIndex = tab switch
        {
            ObjectTab.Roots => 1,
            ObjectTab.Allocation => tabs.Items.Count - 1,
            _ => 0,
        };
        return tabs;
    }

    private static Widget Roots(IReadOnlyList<GcRootPath> paths, Action<NavigationTarget> navigate)
    {
        var tree = new TreeView<RootRow>
        {
            RenderLabel = node => node.Value.Address is ulong address
                ? StyledText.Of(node.Value.Text + " ").Fg(Theme.Current.Foreground)
                    .Append($"0x{address:x}").Fg(Theme.Current.Secondary).Underline().Link(new ObjTarget(address))
                : new StyledText(node.Value.Text, Theme.Current.MutedStyle),
            ShowGuides = true,
            OnLinkClick = payload => navigate(NavigationTarget.FromLink(payload)),
        }.KeepSelectionOnHover();
        if (paths.Count == 0)
        {
            tree.AddRoot(new RootRow("(not reachable from any GC root \u2014 collectable)", null));
        }
        foreach (GcRootPath path in paths)
        {
            TreeNode<RootRow> root = tree.AddRoot(new RootRow($"{path.Root.Kind} {Sherlock.CLI.Rendering.Addresses.Format(path.Root.Address)}", null));
            TreeNode<RootRow> node = root;
            foreach (GcRootNode step in path.Path)
            {
                node = node.AddChild(new RootRow(TypeNames.Short(step.TypeName), step.Address));
            }
            root.ExpandAll();
        }
        tree.MarkDirty();
        Widget keys = OpenOnEnter(tree, node =>
        {
            if (node.Value.Address is ulong address)
            {
                navigate(new ObjTarget(address));
            }
        });
        return Hinted(new Panel(keys, " Why it's alive ") { BorderStyle = BorderStyle.Rounded }, "Enter inspect");
    }

    private static Widget AllocationStack(IReadOnlyList<string> stack, Action<NavigationTarget> navigate)
    {
        if (stack.Count == 0)
        {
            return Message(ProvenanceReader.NoManagedFrames, " Allocation stack ");
        }
        string[] frames = [.. stack];
        Array.Reverse(frames);
        Table table = Table(("Method", Constraint.Fill(3), false), ("Namespace", Constraint.Fill(2), false));
        SetRows(table, frames, frame => [FrameNames.ShortMethod(frame), TypeNames.Namespace(FrameNames.Split(frame).Type)],
            frame => navigate(new MethodTarget(frame)));
        table.Sortable = false;
        return Hinted(new Panel(table, " Allocation stack ") { BorderStyle = BorderStyle.Rounded }, "Enter open method");
    }

    private sealed record RootRow(string Text, ulong? Address);
}
