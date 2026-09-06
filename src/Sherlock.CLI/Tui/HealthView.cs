using System;
using System.Collections.Generic;
using System.Linq;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Cellar.Widgets.Charts;
using Sherlock.Core;
using Sherlock.Core.Diagnostics;
using static Sherlock.CLI.Tui.ViewFormatting;

namespace Sherlock.CLI.Tui;

internal static class HealthView
{
    public static Widget Create(IReadOnlyList<HeapTypeStat> histogram, IReadOnlyList<Finding> findings, string id,
        Action<NavigationTarget> navigate)
    {
        List<HeapTypeStat> types = histogram.OrderByDescending(s => s.TotalSize).ToList();
        long total = Math.Max(1, types.Sum(s => (long)s.TotalSize));
        Color[] palette = Rendering.Theme.ChartColors();
        var bar = new ProportionBar();
        var legend = new Legend { Horizontal = true };
        long shown = 0;
        for (int i = 0; i < Math.Min(6, types.Count); i++)
        {
            Color color = palette[i % palette.Length];
            long bytes = (long)types[i].TotalSize;
            string name = TypeNames.Short(types[i].TypeName);
            bar.Segments.Add(new Segment(name, bytes, color));
            legend.Items.Add(new LegendItem(name, color, $"{100.0 * bytes / total:0}%"));
            shown += bytes;
        }
        if (total > shown)
        {
            bar.Segments.Add(new Segment("other", total - shown, Theme.Current.Muted));
            legend.Items.Add(new LegendItem("other", Theme.Current.Muted, $"{100.0 * (total - shown) / total:0}%"));
        }

        var rows = new Stack(Direction.Vertical);
        foreach (Finding finding in findings)
        {
            (string glyph, Color color) = finding.Severity switch
            {
                FindingSeverity.High => ("\u25cf", Theme.Current.Error),
                FindingSeverity.Warning => ("\u25cf", Theme.Current.Warning),
                _ => ("\u25cb", Theme.Current.Muted),
            };
            rows.Add(new Label(StyledText.Of($"{glyph} ").Fg(color).Append(finding.Title).Fg(Theme.Current.Foreground)), Constraint.Length(1));
            rows.Add(new Label(new StyledText("   " + finding.Detail, Theme.Current.MutedStyle)), Constraint.Length(1));

            NavigationTarget? target = finding.Address is ulong address
                ? new ObjTarget(address, finding.Category.Contains("event", StringComparison.OrdinalIgnoreCase) ? ObjectTab.Roots : ObjectTab.Inspect)
                : finding.Type is { } type ? new TypeTarget(type) : null;
            if (finding.NextCommand is { } next)
            {
                StyledText text = StyledText.Of("   \u2192 ").Fg(Theme.Current.Muted).Append(next).Fg(Theme.Current.Accent);
                if (target is not null)
                {
                    text.Underline().Link(target);
                }
                var line = new Label(text) { OnLinkClick = payload => navigate(NavigationTarget.FromLink(payload)) };
                rows.Add(line, Constraint.Length(1));
            }
        }

        var body = new Stack(Direction.Vertical)
            .Add(new Label(StyledText.Empty()), Constraint.Length(1))
            .Add(new Label(StyledText.Of("Heap composition").Bold().Fg(Theme.Current.Accent)), Constraint.Length(1))
            .Add(bar, Constraint.Length(1))
            .Add(legend, Constraint.Length(1))
            .Add(new Label(StyledText.Empty()), Constraint.Length(1))
            .Add(new Label(StyledText.Of("What looks wrong").Bold().Fg(Theme.Current.Accent)), Constraint.Length(1))
            .Add(new ScrollView(rows), Constraint.Fill());
        return Hinted(new Panel(new Padding(body, new Thickness(1, 0)), $" {id} \u2014 {ByteFormat.Human(total)} on the heap ") { BorderStyle = BorderStyle.Rounded },
            "click a \u2192 to jump  \u00b7  Tab switch lens  \u00b7  q quit");
    }
}
