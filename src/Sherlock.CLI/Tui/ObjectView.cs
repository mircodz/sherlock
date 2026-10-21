using System;
using System.Collections.Generic;
using System.IO;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Cellar.Widgets.Charts.Trees;
using Sherlock.Core;
using Sherlock.Core.Profiling;
using Sherlock.Heap;
using Sherlock.Heap.Analysis;
using static Sherlock.CLI.Tui.ViewFormatting;

namespace Sherlock.CLI.Tui;

internal static class ObjectView
{
    public static Widget Create(OpenedSnapshot opened, ulong address, ObjectTab tab, Action<NavigationTarget> navigate)
    {
        ObjectValue value = opened.Snapshot.Heap.InspectValue(address);
        Tabs tabs = Views()
            .Add("Inspect", () => new ObjectInspectorView(value, opened.Snapshot.Heap.InspectChildren,
                target => navigate(new ObjTarget(target)), type => navigate(new TypeTarget(type))))
            .Add("GC roots", opened.Background((snapshot, cancellation) => Roots(snapshot.Heap.Roots(address, cancellation), navigate)));

        if (opened.Snapshot.HasCorrelation)
        {
            try
            {
                if (opened.Snapshot.WhoAllocated(address) is { } frames)
                {
                    tabs.Add("Allocation stack", () => AllocationStack(frames, navigate));
                }
            }
            catch (InvalidDataException ex)
            {
                tabs.Add("Allocation stack", Message("Allocation stack", ex.Message));
            }
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
            OnActivate = node =>
            {
                if (node.Value.Address is ulong address)
                {
                    navigate(new ObjTarget(address));
                }
            },
        };
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
                node = node.AddChild(new RootRow(Rendering.DisplayNames.Type(step.TypeName), step.Address));
            }
            root.ExpandAll();
        }
        tree.MarkDirty();
        return View("GC roots", tree, "Enter inspect");
    }

    private static Widget AllocationStack(IReadOnlyList<MethodFrame> stack, Action<NavigationTarget> navigate)
    {
        if (stack.Count == 0)
        {
            return Message("Allocation stack", ProvenanceReader.NoManagedFrames);
        }
        MethodFrame[] frames = [.. stack];
        Array.Reverse(frames);
        Table<MethodFrame> table = Table(frames, frame => navigate(new MethodTarget(frame)),
            NameColumn<MethodFrame>("Method", frame => frame.Label),
            NamespaceColumn<MethodFrame>(frame => Printable.Escape(frame.Namespace)));
        table.Sortable = false; // the order is the stack
        return View("Allocation stack", table, "Enter open method");
    }

    private sealed record RootRow(string Text, ulong? Address);
}
