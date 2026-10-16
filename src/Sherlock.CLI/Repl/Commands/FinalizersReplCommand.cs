using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Reports objects awaiting finalization, grouped by type.</summary>
public sealed class FinalizersReplCommand : IReplCommand
{
    private const int DefaultLimit = 20;

    public string Name => "finalizers";
    public IReadOnlyList<string> Aliases => ["fin"];
    public string Summary => "Objects awaiting finalization by type (a missed-Dispose heuristic).";
    public string Usage => "finalizers [count]";
    public int MaxArgs => 1;

    public ReplResult Execute(ReplContext context, string[] args)
    {
        int limit = Args.Count(args, 0, DefaultLimit, Usage);

        FinalizerReport report = context.Console.Status()
            .Start("Scanning finalizer queue…", _ => context.Snapshot.Finalizers(context.Cancellation));

        if (report.TotalObjects == 0)
        {
            context.Console.MarkupLine($"[{Palette.Hot}]No finalizable objects.[/] [{Palette.Muted}]Nothing is waiting on the finalizer queue.[/]");
            return ReplResult.Success;
        }

        var table = Theme.Table(expand: true);
        table.AddColumn(new TableColumn("[bold]Count[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Bytes[/]").RightAligned());
        table.AddColumn("[bold]Type[/]");
        table.AddColumn("[bold]Namespace[/]");

        foreach (FinalizableTypeStat stat in report.ByType.Take(limit))
        {
            table.AddRow(
                $"[bold]{Counts.Compact(stat.Count)}[/]",
                Styled.HotSize((long)stat.TotalBytes),
                Styled.Type(stat.TypeName),
                Styled.Namespace(stat.TypeName));
        }

        context.Console.Write(table);
        context.Console.MarkupLineInterpolated(
            $"[{Palette.Muted}]{Counts.Format(report.TotalObjects)} finalizable objects,[/] [{Palette.Text}]{ByteSize.Format((long)report.TotalBytes)}[/][{Palette.Muted}]. A live finalizer usually means Dispose() wasn't called; list a type with[/] objects <type>[{Palette.Muted}].[/]");
        return ReplResult.Success;
    }
}
