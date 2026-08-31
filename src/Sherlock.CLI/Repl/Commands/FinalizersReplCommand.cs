using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Objects still registered for finalization, by type (a "forgot to Dispose" heuristic; Dispose calls GC.SuppressFinalize to drop off this queue).</summary>
public sealed class FinalizersReplCommand : IReplCommand
{
    private const int DefaultLimit = 20;

    public string Name => "finalizers";
    public IReadOnlyList<string> Aliases => ["fin"];
    public string Summary => "Objects awaiting finalization by type (a missed-Dispose heuristic).";
    public string Usage => "finalizers [count]";

    public void Execute(ReplContext context, string[] args)
    {
        int limit = Args.Limit(args, 0, DefaultLimit);

        FinalizerReport report = context.Console.Status()
            .Start("Scanning finalizer queue…", _ => context.Snapshot.Finalizers());

        if (report.TotalObjects == 0)
        {
            context.Console.MarkupLine("[#AFFF00]No finalizable objects.[/] [#808791]Nothing is waiting on the finalizer queue.[/]");
            return;
        }

        var table = Theme.Table(expand: true);
        table.AddColumn(new TableColumn("[bold]Count[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Bytes[/]").RightAligned());
        table.AddColumn("[bold]Type[/]");

        foreach (FinalizableTypeStat stat in report.ByType.Take(limit))
        {
            table.AddRow(
                $"[bold]{Counts.Compact(stat.Count)}[/]",
                $"[#F2F2F2]{ByteSize.Format((long)stat.TotalBytes)}[/]",
                $"[#00D7FF]{Markup.Escape(TypeNames.Short(stat.TypeName))}[/]");
        }

        context.Console.Write(table);
        context.Console.MarkupLineInterpolated(
            $"[#808791]{Counts.Format(report.TotalObjects)} finalizable objects,[/] [#F2F2F2]{ByteSize.Format((long)report.TotalBytes)}[/][#808791]. A live finalizer usually means Dispose() wasn't called; list a type with[/] objects <type>[#808791].[/]");
    }
}
