using System;
using System.Collections.Generic;
using System.Linq;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Text;
using Cellar.Widgets;
using Cellar.Widgets.Charts;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using static Sherlock.CLI.Tui.ViewFormatting;
using Theme = Cellar.Theming.Theme;

namespace Sherlock.CLI.Tui;

/// <summary>What Health shows besides the findings. All of it is cheap to read when a snapshot opens.</summary>
/// <param name="Runtime">The runtime and GC mode, e.g. "Core 10.0.326.7603 · workstation GC".</param>
/// <param name="Captured">When and why the snapshot was taken, e.g. "10-10 00:59 · on exit".</param>
internal sealed record HeapOverview(string Id, IReadOnlyList<HeapTypeStat> Histogram, HeapGenerations? Generations = null,
    string? Runtime = null, string? Captured = null);

internal static class HealthView
{
    private const int TypesInBar = 5;
    private const int LegendNameWidth = 28;

    public static Widget Create(HeapOverview overview)
    {
        var body = new Stack(Direction.Vertical)
            .Add(new Label(StyledText.Empty()), Constraint.Length(1))
            .Add(new Label(Summary(overview.Histogram)), Constraint.Length(1));
        if (overview.Runtime is { } runtime)
        {
            body.Add(new Label(new StyledText(runtime, Theme.Current.MutedStyle)), Constraint.Length(1));
        }
        if (overview.Generations is { Total: > 0 } generations)
        {
            Section(body, "Heap by generation", ByGeneration(generations));
        }
        if (overview.Histogram.Any(stat => stat.TypeName != FreeType && stat.TotalSize > 0))
        {
            Section(body, "Objects by type", ByType(overview.Histogram));
        }

        string title = overview.Captured is { } captured ? $" {overview.Id} \u00b7 {captured} " : $" {overview.Id} ";
        return Hinted(new Panel(new Padding(body, new Thickness(1, 0)), title) { BorderStyle = BorderStyle.Rounded }, "");
    }

    private const string FreeType = "Free";

    // Heap size counts free space, as the generations do; objects and types don't.
    private static StyledText Summary(IReadOnlyList<HeapTypeStat> histogram)
    {
        long heap = histogram.Sum(stat => (long)stat.TotalSize);
        long free = histogram.Where(stat => stat.TypeName == FreeType).Sum(stat => (long)stat.TotalSize);
        List<HeapTypeStat> types = histogram.Where(stat => stat.TypeName != FreeType).ToList();
        StyledText text = StyledText.Of("Heap ").Fg(Theme.Current.Muted).Append(ByteFormat.Human(heap)).Bold().Fg(Theme.Current.Foreground)
            .Append($"   {types.Sum(stat => stat.Count):N0}").Fg(Theme.Current.Foreground).Append(" objects").Fg(Theme.Current.Muted)
            .Append($"   {types.Count:N0}").Fg(Theme.Current.Foreground).Append(" types").Fg(Theme.Current.Muted);
        if (heap > 0)
        {
            text.Append($"   {ByteFormat.Human(free)}").Fg(Theme.Current.Foreground)
                .Append($" free ({100.0 * free / heap:0}%)").Fg(Theme.Current.Muted);
        }
        return text;
    }

    private static void Section(Stack body, string heading, (ProportionBar Bar, Legend Legend) chart) =>
        body.Add(new Label(StyledText.Empty()), Constraint.Length(1))
            .Add(new Label(StyledText.Of(heading).Bold().Fg(Theme.Current.Info)), Constraint.Length(1))
            .Add(chart.Bar, Constraint.Length(1))
            .Add(chart.Legend, Constraint.Length(1));

    private static (ProportionBar, Legend) ByGeneration(HeapGenerations generations)
    {
        (string Name, ulong Bytes, string Color)[] parts =
        [
            ("gen0", generations.Gen0, Palette.Hot),
            ("gen1", generations.Gen1, Palette.Name),
            ("gen2", generations.Gen2, Palette.Heading),
            ("LOH", generations.Large, Palette.Magenta),
            ("POH", generations.Pinned, Palette.Address),
            ("frozen", generations.Frozen, Palette.Muted),
        ];
        var bar = new ProportionBar();
        var legend = new Legend { Horizontal = true };
        foreach ((string name, ulong bytes, string hex) in parts)
        {
            if (name == "frozen" && bytes == 0)
            {
                continue;
            }
            Color color = Color.Hex(hex);
            if (bytes > 0)
            {
                bar.Segments.Add(new Segment(name, (long)bytes, color));
            }
            legend.Items.Add(new LegendItem(name, color, ByteFormat.Human(bytes)));
        }
        return (bar, legend);
    }

    // Shares of the bytes in objects; free space isn't a type and has its own place in the summary.
    private static (ProportionBar, Legend) ByType(IReadOnlyList<HeapTypeStat> histogram)
    {
        List<HeapTypeStat> types = histogram.Where(stat => stat.TypeName != FreeType).OrderByDescending(stat => stat.TotalSize).ToList();
        long total = Math.Max(1, types.Sum(stat => (long)stat.TotalSize));
        Color[] palette = Sherlock.CLI.Rendering.Theme.ChartColors();
        var bar = new ProportionBar();
        var legend = new Legend { Horizontal = true };
        long shown = 0;
        for (int i = 0; i < Math.Min(TypesInBar, types.Count); i++)
        {
            Color color = palette[i % palette.Length];
            long bytes = (long)types[i].TotalSize;
            string name = TypeNames.Short(types[i].TypeName);
            bar.Segments.Add(new Segment(name, bytes, color));
            legend.Items.Add(new LegendItem(TextUtil.Preview(name, LegendNameWidth), color, $"{100.0 * bytes / total:0}%"));
            shown += bytes;
        }
        if (total > shown)
        {
            bar.Segments.Add(new Segment("other", total - shown, Theme.Current.Muted));
            legend.Items.Add(new LegendItem("other", Theme.Current.Muted, $"{100.0 * (total - shown) / total:0}%"));
        }
        return (bar, legend);
    }
}
