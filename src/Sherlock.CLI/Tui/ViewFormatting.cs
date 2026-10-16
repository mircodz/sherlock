using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Sherlock.Core;
using Sherlock.Core.Store;

namespace Sherlock.CLI.Tui;

internal static class ViewFormatting
{
    /// <summary>A page's views, one row of tabs. Tab and Shift+Tab switch views, digits jump to one, and the arrows
    /// stay inside the view, also where it doesn't use them. A view never contains tabs of its own.</summary>
    public static Tabs Views() => new() { ArrowKeysSwitch = false };

    /// <summary>A sortable table: Enter or a second click opens a row, and a click on a header sorts by its column.</summary>
    public static Table<T> Table<T>(IEnumerable<T> rows, Action<T> open, params TableColumn<T>[] columns)
    {
        var table = new Table<T> { ShowHeader = true, Striped = true, ShowScrollbar = true, Sortable = true, OnActivate = open };
        table.Columns.AddRange(columns);
        table.SetRows(rows);
        return table;
    }

    // Every table lays out its columns the same way, as the trees do: the name, its namespace, the main size and its
    // share, then other sizes and counts. Columns use the colors of the matching tree columns.

    /// <summary>The name of what a row is about, such as a type or a method.</summary>
    public static TableColumn<T> NameColumn<T>(string header, Func<T, string> name, int share = 3) =>
        TextColumn(header, Constraint.Fill(share), name, Theme.Current.Accent);

    public static TableColumn<T> NamespaceColumn<T>(Func<T, string> name) =>
        TextColumn("Namespace", Constraint.Fill(2), name, Theme.Current.Muted);

    public static TableColumn<T> TextColumn<T>(string header, Constraint width, Func<T, string> text, Color color) =>
        new(header, width, row => new StyledText(text(row), Color(color)));

    public static TableColumn<T> BytesColumn<T>(string header, Func<T, long> bytes, Color color, int width = 11) =>
        new(header, Constraint.Length(width), row => new StyledText(ByteFormat.Column(bytes(row)), Color(color)), Justify.Right)
        {
            SortKey = row => bytes(row),
        };

    public static TableColumn<T> CountColumn<T>(string header, Func<T, long> count, int width = 10) =>
        new(header, Constraint.Length(width), row => new StyledText(count(row).ToString("N0", CultureInfo.InvariantCulture), Theme.Current.MutedStyle),
            Justify.Right)
        {
            SortKey = row => count(row),
        };

    /// <summary>A share as a number; a large share stands out, as in the trees.</summary>
    public static TableColumn<T> PercentColumn<T>(Func<T, double> percent) =>
        new("%", Constraint.Length(6), row =>
        {
            double value = percent(row);
            return new StyledText(value.ToString("0.0", CultureInfo.InvariantCulture), Color(value >= 10 ? Theme.Current.Success : Theme.Current.Muted));
        }, Justify.Right)
        {
            SortKey = row => percent(row),
        };

    private static Style Color(Color foreground) => new(foreground, Cellar.Primitives.Color.Default);

    /// <summary>A view: <paramref name="body"/> framed under <paramref name="title"/>, its name and at most a figure or
    /// two ("Hot methods \u00b7 top 200 of 1,204"), above the screen's one hint bar. <paramref name="hint"/> lists the
    /// view's own keys; the keys every screen shares are appended so they read the same everywhere. Only the library is
    /// neither tabbed nor nested.</summary>
    public static Widget View(string title, Widget body, string hint, bool tabbed = true, bool canGoBack = true) =>
        new Stack(Direction.Vertical)
            .Add(new Panel(body, $" {title} "), Constraint.Fill())
            .Add(new Label(new StyledText("  " + string.Join(HintSeparator,
                new[] { hint, tabbed ? "Tab switch view" : null, canGoBack ? "Backspace back" : null, "q quit" }
                    .Where(part => !string.IsNullOrEmpty(part))),
                Theme.Current.MutedStyle)), Constraint.Length(1));

    private const string HintSeparator = "  \u00b7  ";

    /// <summary>A view that only explains something, such as why it's empty.</summary>
    public static Widget Message(string title, string text) =>
        View(title, new Padding(new Label(new StyledText(text, Theme.Current.MutedStyle)), new Thickness(1)), "");

    /// <summary>What a snapshot holds: "heap", "heap + allocations", or "heap + allocations + whoalloc".</summary>
    public static string SnapshotKind(SnapshotEntry snapshot) =>
        snapshot.HasCorrelation ? "heap + allocations + whoalloc" : snapshot.HasAllocations ? "heap + allocations" : "heap";

    /// <summary>What a snapshot holds and what it was taken on: "heap + allocations \u00b7 on exit".</summary>
    public static string SnapshotContents(SnapshotEntry snapshot) =>
        snapshot.Reason is { } reason ? $"{SnapshotKind(snapshot)} \u00b7 on {reason}" : SnapshotKind(snapshot);

    /// <summary>"1 type", "3 types".</summary>
    public static string Count(long count, string noun) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}{(count == 1 ? "" : "s")}";
}
