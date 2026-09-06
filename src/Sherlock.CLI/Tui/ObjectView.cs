using System;
using System.Collections.Generic;
using System.Threading;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Cellar.Widgets.Charts.Trees;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using static Sherlock.CLI.Tui.ViewFormatting;

namespace Sherlock.CLI.Tui;

internal static class ObjectView
{
    public static Widget Create(Snapshot snapshot, ulong address, ObjectTab tab, Action<NavigationTarget> navigate)
    {
        ObjectValue value = snapshot.InspectValue(address);
        var tabs = new Tabs()
            .Add("Inspect", new ObjectInspectorView(value, snapshot.InspectChildren,
                target => navigate(new ObjTarget(target)), type => navigate(new TypeTarget(type))))
            .Add("GC roots", () => new AsyncContent(cancellation => Roots(snapshot.Roots(address, cancellation), navigate)));

        if (snapshot.HasCorrelation && snapshot.WhoAllocated(address) is { } stack)
        {
            tabs.Add("whoalloc", () => AllocationStack(stack, navigate));
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
                    .Append($"@0x{address:x}").Fg(Theme.Current.Accent).Underline().Link(new ObjTarget(address))
                : new StyledText(node.Value.Text, Theme.Current.MutedStyle),
            ShowGuides = true,
            OnLinkClick = payload => navigate(NavigationTarget.FromLink(payload)),
        };
        if (paths.Count == 0)
        {
            tree.AddRoot(new RootRow("(not reachable from any GC root \u2014 collectable)", null));
        }
        foreach (GcRootPath path in paths)
        {
            TreeNode<RootRow> root = tree.AddRoot(new RootRow($"{path.Root.Kind} @ 0x{path.Root.Address:x12}", null));
            TreeNode<RootRow> node = root;
            foreach (GcRootNode step in path.Path)
            {
                node = node.AddChild(new RootRow(TypeNames.Short(step.TypeName), step.Address));
            }
            root.ExpandAll();
        }
        tree.MarkDirty();
        return Hinted(new Panel(tree, " Why it's alive \u2014 click a holder to inspect it ") { BorderStyle = BorderStyle.Rounded },
            "Tab switch view  \u00b7  click a holder to inspect it  \u00b7  Backspace back");
    }

    private static Widget AllocationStack(string stack, Action<NavigationTarget> navigate)
    {
        string[] frames = stack.Split(';');
        Array.Reverse(frames);
        Table table = Table(("Method", Constraint.Fill(2), false), ("Namespace", Constraint.Fill(3), false));
        SetRows(table, frames, frame =>
        {
            int dot = frame.LastIndexOf('.');
            return [dot >= 0 ? frame[(dot + 1)..] : frame, dot >= 0 ? frame[..dot] : ""];
        }, frame => navigate(new MethodTarget(frame)));
        table.Sortable = false;
        return Hinted(new Panel(table, " Allocation stack \u2014 Enter a frame for its callers ") { BorderStyle = BorderStyle.Rounded },
            "Enter \u2192 callers of this frame  \u00b7  Backspace back");
    }

    private sealed record RootRow(string Text, ulong? Address);
}
