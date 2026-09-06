using System;
using System.Globalization;
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
    public static Widget Create(DominatorTree dominators, Action<NavigationTarget> navigate)
    {
        long total = Math.Max(1, (long)dominators.TotalReachableBytes);
        var tree = new TreeView<DominatorNode>
        {
            RenderLabel = node => StyledText.Of(TypeNames.Short(node.Value.TypeName)).Fg(Theme.Current.Accent).Underline().Link(new TypeTarget(node.Value.TypeName))
                .Append("  @").Fg(Theme.Current.Muted)
                .Append($"0x{node.Value.Address:x}").Fg(Theme.Current.Secondary).Underline().Link(new ObjTarget(node.Value.Address)),
            ShowHeader = true,
            ShowGuides = true,
            Striped = true,
            OnLinkClick = payload => navigate(NavigationTarget.FromLink(payload)),
            OnActivate = node => navigate(new ObjTarget(node.Value.Address)),
        };
        tree.Columns.Add(new TreeColumn<DominatorNode>("Retained", 11,
            node => new StyledText(ByteFormat.Human(node.Value.RetainedSize), new Style(Theme.Current.Success, Color.Default))));
        tree.Columns.Add(new TreeColumn<DominatorNode>("%", 6, node =>
        {
            double percent = 100.0 * (long)node.Value.RetainedSize / total;
            Color color = percent >= 10 ? Theme.Current.Success : Theme.Current.Muted;
            return new StyledText(percent.ToString("0.0", CultureInfo.InvariantCulture), new Style(color, Color.Default));
        }));
        tree.Columns.Add(new TreeColumn<DominatorNode>("Own", 10,
            node => new StyledText(ByteFormat.Human(node.Value.OwnSize), Theme.Current.MutedStyle)));
        foreach (DominatorNode node in dominators.TopDominators(25))
        {
            tree.AddRoot(node, parent => dominators.ImmediateChildren(parent.Address, 20));
        }
        return Hinted(new Panel(tree, " Retention \u2014 what holds the memory ") { BorderStyle = BorderStyle.Rounded },
            "\u2192/\u2190 expand   \u00b7   click a type or address   \u00b7   Enter inspect   \u00b7   Backspace back");
    }
}
