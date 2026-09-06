using System;
using System.Collections.Generic;
using System.Globalization;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;

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

    public static Widget Hinted(Widget body, string hint) =>
        new Stack(Direction.Vertical)
            .Add(body, Constraint.Fill())
            .Add(new Label(new StyledText("  " + hint, Theme.Current.MutedStyle)), Constraint.Length(1));

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
