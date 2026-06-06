using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Lists managed heap objects grouped by type (SOS <c>dumpheap -stat</c> style).</summary>
public sealed class DumpHeapReplCommand : IReplCommand
{
    private const int DefaultLimit = 40;

    public string Name => "dumpheap";
    public IReadOnlyList<string> Aliases => new[] { "dh", "heap" };
    public string Summary => "Show heap object statistics by type, largest first.";
    public string Usage => "dumpheap [type-filter]";

    public void Execute(ReplContext context, string[] args)
    {
        string? filter = args.Length > 0 ? args[0] : null;

        IReadOnlyList<HeapTypeStat> stats = new HeapAnalyzer(context.Session).GetStatistics(filter);

        if (stats.Count == 0)
        {
            context.Console.MarkupLine(filter is null
                ? "[yellow]No objects found on the heap.[/]"
                : $"[yellow]No types matched[/] '{Markup.Escape(filter)}'.");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn(new TableColumn("[bold]Type[/]"));
        table.AddColumn(new TableColumn("[bold]Count[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Total[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Avg[/]").RightAligned());

        long totalCount = 0;
        ulong totalSize = 0;
        foreach (HeapTypeStat stat in stats)
        {
            totalCount += stat.Count;
            totalSize += stat.TotalSize;
        }

        foreach (HeapTypeStat stat in stats.Take(DefaultLimit))
        {
            table.AddRow(
                Markup.Escape(stat.TypeName),
                stat.Count.ToString("N0"),
                ByteSize.Format((long)stat.TotalSize),
                ByteSize.Format((long)stat.AverageSize));
        }

        context.Console.Write(table);

        if (stats.Count > DefaultLimit)
        {
            context.Console.MarkupLine($"[grey]… {stats.Count - DefaultLimit:N0} more types not shown. Filter with[/] dumpheap <type>.");
        }

        context.Console.MarkupLine(
            $"[bold]{stats.Count:N0}[/] types, [bold]{totalCount:N0}[/] objects, [bold]{ByteSize.Format((long)totalSize)}[/] total.");
    }
}
