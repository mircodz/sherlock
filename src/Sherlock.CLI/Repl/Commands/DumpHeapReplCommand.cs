using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Lists managed heap objects grouped by type (SOS <c>dumpheap -stat</c> style).</summary>
public sealed class DumpHeapReplCommand : IReplCommand
{
    private const int DefaultLimit = 40;

    public string Name => "dumpheap";
    public IReadOnlyList<string> Aliases => ["dh", "heap"];
    public string Summary => "Show heap object statistics by type, largest first.";
    public string Usage => "dumpheap [type-filter]";

    public void Execute(ReplContext context, string[] args)
    {
        string? filter = args.Length > 0 ? args[0] : null;

        // Cached full histogram, filtered in-memory to avoid re-enumeration.
        IReadOnlyList<HeapTypeStat> stats = context.Snapshot.Histogram;
        if (filter is not null)
        {
            stats = stats.Where(s => s.TypeName.Contains(filter, System.StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (stats.Count == 0)
        {
            context.Console.MarkupLine(filter is null
                ? "[#FFAF00]No objects found on the heap.[/]"
                : $"[#FFAF00]No types matched[/] '{Markup.Escape(filter)}'.");
            return;
        }

        var table = Theme.Table(expand: true);
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
                $"[#00D7FF]{Markup.Escape(TypeNames.Short(stat.TypeName))}[/]",
                Counts.Format(stat.Count),
                $"[bold #AFFF00]{ByteSize.Format((long)stat.TotalSize)}[/]",
                ByteSize.Format((long)stat.AverageSize));
        }

        context.Console.Write(table);

        if (stats.Count > DefaultLimit)
        {
            context.Console.MarkupLine($"[#808791]… {Counts.Format(stats.Count - DefaultLimit)} more types not shown. Filter with[/] dumpheap <type>.");
        }

        context.Console.MarkupLine(
            $"[bold]{Counts.Format(stats.Count)}[/] types, [bold]{Counts.Format(totalCount)}[/] objects, [bold #AFFF00]{ByteSize.Format((long)totalSize)}[/] total.");
    }
}
