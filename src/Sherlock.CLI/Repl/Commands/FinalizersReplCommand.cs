using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// Lists objects still registered for finalization, by type - a "forgot to Dispose" heuristic
/// (a proper Dispose calls GC.SuppressFinalize, which takes the object off this queue).
/// </summary>
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
            context.Console.MarkupLine("[green]No finalizable objects.[/] [grey]Nothing is waiting on the finalizer queue.[/]");
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
                $"[green]{ByteSize.Format((long)stat.TotalBytes)}[/]",
                $"[aqua]{Markup.Escape(TypeNames.Short(stat.TypeName))}[/]");
        }

        context.Console.Write(table);
        context.Console.MarkupLineInterpolated(
            $"[grey]{report.TotalObjects:N0} finalizable objects,[/] [green]{ByteSize.Format((long)report.TotalBytes)}[/][grey]. A live finalizer usually means Dispose() wasn't called; list a type with[/] objects <type>[grey].[/]");
    }
}
