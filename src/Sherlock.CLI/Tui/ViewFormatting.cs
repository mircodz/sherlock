using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Terminal;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Cellar.Widgets.Charts.Trees;

namespace Sherlock.CLI.Tui;

internal static class ViewFormatting
{
    public static Table Table(params (string Header, Constraint Width, bool Right)[] columns)
    {
        var table = new Table { ShowHeader = true, SelectedIndex = 0, Striped = true, ShowScrollbar = true, Sortable = true };
        foreach ((string header, Constraint width, bool right) in columns)
        {
            table.Columns.Add(new Column(header, width, right ? Justify.Right : Justify.Left) { SortKey = right ? text => ParseNumber(text) : null });
        }
        return table;
    }

    /// <summary>The screen's one hint bar: <paramref name="hint"/> lists the view's own keys; the keys every screen
    /// shares are appended here so they read the same everywhere. Only the library is neither tabbed nor nested.</summary>
    public static Widget Hinted(Widget body, string hint, bool tabbed = true, bool canGoBack = true) =>
        new Stack(Direction.Vertical)
            .Add(body, Constraint.Fill())
            .Add(new Label(new StyledText("  " + string.Join(HintSeparator,
                new[] { hint, tabbed ? "Tab switch view" : null, canGoBack ? "Backspace back" : null, "q quit" }
                    .Where(part => !string.IsNullOrEmpty(part))),
                Theme.Current.MutedStyle)), Constraint.Length(1));

    private const string HintSeparator = "  \u00b7  ";

    /// <summary>A view that only explains something, such as why it's empty.</summary>
    public static Widget Message(string text, string title) =>
        Hinted(new Panel(new Padding(new Label(new StyledText(text, Theme.Current.MutedStyle)), new Thickness(1)), title)
            { BorderStyle = BorderStyle.Rounded }, "");

    /// <summary>Adds a view, built the first time it's shown. Every tab goes through here or
    /// <see cref="AddBackgroundView"/>, which give all views the same keys (see <see cref="View"/>). A page has one row
    /// of tabs: a view never contains tabs of its own.</summary>
    public static Tabs AddView(this Tabs tabs, string name, Func<Widget> build) => tabs.Add(name, () => View.Of(build()));

    /// <summary>Adds a view whose content is slow to build, so it's built in the background.</summary>
    public static Tabs AddBackgroundView(this Tabs tabs, string name, Func<CancellationToken, Widget> build) =>
        tabs.Add(name, () => View.Background(build));

    /// <summary>Enter opens the selected node. Cellar's tree also toggles the node on Enter, so it would be collapsed
    /// or expanded when you come back; ←/→ and Space still do that.</summary>
    public static Widget OpenOnEnter<T>(TreeView<T> tree, Action<TreeNode<T>> open) => new KeyHook(tree, key =>
    {
        if (key.Key != Key.Enter || tree.SelectedNode is not { } node)
        {
            return null;
        }
        open(node);
        return true;
    });

    /// <summary>Keeps the selection's colors when the mouse is over a link on the selected row. Cellar paints a hovered
    /// link with the stripe background unless the link brings its own hover style, so the highlight flickered off.
    /// Call after setting <c>RenderLabel</c>.</summary>
    public static TreeView<T> KeepSelectionOnHover<T>(this TreeView<T> tree)
    {
        Func<TreeNode<T>, StyledText> render = tree.RenderLabel;
        tree.RenderLabel = node => ReferenceEquals(node, tree.SelectedNode) ? WithHoverStyle(render(node), SelectedLinkHover) : render(node);
        return tree;
    }

    private static Style SelectedLinkHover =>
        new(Theme.Current.SelectionForeground, Theme.Current.SelectionBackground, TextAttributes.Bold | TextAttributes.Underline);

    private static StyledText WithHoverStyle(StyledText text, Style hover)
    {
        if (!text.HasLinks)
        {
            return text;
        }
        StyledText result = StyledText.Empty();
        foreach (Span span in text.Spans)
        {
            result.Append(span.Link switch
            {
                null => span,
                LinkInfo link => span.WithLink(link with { HoverStyle = hover }),
                object payload => span.WithLink(new LinkInfo(payload, hover)),
            });
        }
        return result;
    }

    public static void SetRows<T>(Table table, IEnumerable<T> values, Func<T, string[]> render, Action<T> activate)
    {
        var byRow = new Dictionary<string[], T>();
        table.Rows.Clear();
        foreach (T value in values)
        {
            string[] row = render(value);
            table.Rows.Add(row);
            byRow.Add(row, value);
        }
        // Cellar sorts the row arrays in place; navigation must follow the row, not its old index.
        table.OnActivate = index => activate(byRow[table.Rows[index]]);
        table.SelectedIndex = table.Rows.Count > 0 ? 0 : -1;
        table.ScrollOffset = 0;
    }

    /// <summary>"1 type", "3 types".</summary>
    public static string Count(long count, string noun) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}{(count == 1 ? "" : "s")}";

    public static string Bar(double percentage, int width)
    {
        int filled = (int)Math.Round(Math.Clamp(percentage, 0, 100) / 100.0 * width);
        return new string('\u2588', filled) + new string('\u2591', width - filled);
    }

    internal static double ParseNumber(string text)
    {
        text = text.Trim();
        double multiplier = 1;
        foreach ((string suffix, double factor) in Units)
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal))
            {
                multiplier = factor;
                text = text[..^suffix.Length].Trim();
                break;
            }
        }
        return double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out double value)
            ? value * multiplier
            : throw new FormatException($"'{text}' is not a numeric table value.");
    }

    private static readonly (string Suffix, double Factor)[] Units =
    [
        ("PB", 1125899906842624d),
        ("TB", 1099511627776d),
        ("GB", 1073741824d),
        ("MB", 1048576d),
        ("KB", 1024d),
        ("B", 1d),
    ];
}
