using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Shows the GC heap segment layout, grouped by generation/kind.</summary>
public sealed class SegmentsReplCommand : IReplCommand
{
    public string Name => "segments";
    public IReadOnlyList<string> Aliases => new[] { "seg", "eeheap" };
    public string Summary => "Show GC heap segments by generation (gen0/1/2, LOH, POH).";
    public string Usage => "segments";

    public void Execute(ReplContext context, string[] args)
    {
        IReadOnlyList<SegmentInfo> segments = new RuntimeAnalyzer(context.Session).GetSegments();
        if (segments.Count == 0)
        {
            context.Console.MarkupLine("[yellow]No GC segments found.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("[bold]Kind[/]");
        table.AddColumn(new TableColumn("[bold]Start[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]End[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Size[/]").RightAligned());

        foreach (SegmentInfo segment in segments)
        {
            table.AddRow(
                Markup.Escape(segment.Kind),
                $"[grey]0x{segment.Start:x}[/]",
                $"[grey]0x{segment.End:x}[/]",
                ByteSize.Format((long)segment.Length));
        }

        context.Console.Write(table);

        // Roll up totals per kind.
        var byKind = segments
            .GroupBy(s => s.Kind)
            .Select(g => (Kind: g.Key, Size: g.Aggregate(0UL, (acc, s) => acc + s.Length)))
            .OrderByDescending(x => x.Size);

        context.Console.MarkupLine($"[grey]Totals:[/] " +
            string.Join("  ", byKind.Select(k => $"[bold]{Markup.Escape(k.Kind)}[/] {ByteSize.Format((long)k.Size)}")));
    }
}
