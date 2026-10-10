using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cellar.Primitives;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Cellar.Widgets.Charts.Trees;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using static Sherlock.CLI.Tui.ViewFormatting;

namespace Sherlock.CLI.Tui;

internal static class RetentionView
{
    private const int Holders = 25;
    private const int ChildrenShown = 20;

    public static Widget Create(DominatorTree dominators, Action<NavigationTarget> navigate)
    {
        long total = Math.Max(1, (long)dominators.TotalReachableBytes);
        var tree = new TreeView<Row>
        {
            RenderLabel = node => node.Value.Holder is { } holder
                ? StyledText.Of(TypeNames.Short(holder.TypeName)).Fg(Theme.Current.Accent).Underline().Link(new TypeTarget(holder.TypeName))
                    .Append(" ").Fg(Theme.Current.Muted)
                    .Append($"0x{holder.Address:x}").Fg(Theme.Current.Secondary).Underline().Link(new ObjTarget(holder.Address))
                : new StyledText("\u2026 smaller holders", Theme.Current.MutedStyle),
            ShowHeader = true,
            ShowGuides = true,
            Striped = true,
            OnLinkClick = payload => navigate(NavigationTarget.FromLink(payload)),
        }.KeepSelectionOnHover();
        tree.Columns.Add(new TreeColumn<Row>("Retained", 11,
            node => new StyledText(ByteFormat.Column(node.Value.Retained), new Style(Theme.Current.Success, Color.Default))));
        tree.Columns.Add(new TreeColumn<Row>("%", 6, node =>
        {
            double percent = 100.0 * (long)node.Value.Retained / total;
            Color color = percent >= 10 ? Theme.Current.Success : Theme.Current.Muted;
            return new StyledText(percent.ToString("0.0", CultureInfo.InvariantCulture), new Style(color, Color.Default));
        }));
        tree.Columns.Add(new TreeColumn<Row>("Own", 10,
            node => new StyledText(node.Value.Holder is { } holder ? ByteFormat.Column(holder.OwnSize) : "", Theme.Current.MutedStyle)));
        foreach (DominatorNode holder in dominators.TopDominators(Holders))
        {
            tree.AddRoot(new Row(holder, holder.RetainedSize), parent => Children(dominators, parent));
        }
        Widget keys = OpenOnEnter(tree, node =>
        {
            if (node.Value.Holder is { } holder)
            {
                navigate(new ObjTarget(holder.Address));
            }
        });
        return Hinted(new Panel(keys, $" Retention \u2014 the {Holders} objects holding the most memory ") { BorderStyle = BorderStyle.Rounded },
            "\u2192\u2190 expand  \u00b7  Enter inspect");
    }

    // An object retains its own size plus what each child retains, so whatever the shown children don't account
    // for is held by the ones left out.
    private static IEnumerable<Row> Children(DominatorTree dominators, Row parent)
    {
        if (parent.Holder is not { } holder)
        {
            return [];
        }
        IReadOnlyList<DominatorNode> children = dominators.ImmediateChildren(holder.Address, ChildrenShown);
        var rows = children.Select(child => new Row(child, child.RetainedSize)).ToList();
        ulong shown = holder.OwnSize + children.Aggregate(0UL, (sum, child) => sum + child.RetainedSize);
        if (children.Count == ChildrenShown && holder.RetainedSize > shown)
        {
            rows.Add(new Row(null, holder.RetainedSize - shown));
        }
        return rows;
    }

    /// <summary>A holder, or the bytes held by the children too small to list (<see cref="Holder"/> is null).</summary>
    private sealed record Row(DominatorNode? Holder, ulong Retained);
}
