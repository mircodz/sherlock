using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Shows the objects with the largest retained size (the MAT dominator tree top view).</summary>
public sealed class DominatorsReplCommand : IReplCommand
{
    private const int DefaultLimit = 25;

    public string Name => "dominators";
    public IReadOnlyList<string> Aliases => ["dom", "retainers"];
    public string Summary => "Show objects with the largest retained size (biggest memory holders).";
    public string Usage => "dominators [count]";

    public void Execute(ReplContext context, string[] args)
    {
        int limit = args.Length > 0 && int.TryParse(args[0], out int n) && n > 0 ? n : DefaultLimit;

        DominatorTree tree = context.Console.Status()
            .Start("Building dominator tree…", _ => context.Session.GetDominatorTree());

        IReadOnlyList<DominatorNode> top = tree.TopDominators(limit);
        ulong total = tree.TotalReachableBytes;

        var table = new Table().Border(TableBorder.Square).Expand();
        table.AddColumn("[bold]Address[/]");
        table.AddColumn(new TableColumn("[bold]Retained[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]%[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Shallow[/]").RightAligned());
        table.AddColumn("[bold]Type[/]");

        foreach (DominatorNode node in top)
        {
            double pct = total == 0 ? 0 : 100.0 * node.RetainedSize / total;
            table.AddRow(
                $"[grey]0x{node.Address:x}[/]",
                $"[bold green]{ByteSize.Format((long)node.RetainedSize)}[/]",
                $"{pct:0.0}%",
                ByteSize.Format((long)node.OwnSize),
                $"[aqua]{Markup.Escape(TypeNames.Short(node.TypeName))}[/]");
        }

        context.Console.Write(table);
        context.Console.MarkupLine(
            $"[grey]{tree.ObjectCount:N0} reachable objects,[/] [bold green]{ByteSize.Format((long)total)}[/] [grey]retained from roots. " +
            $"Drill in with[/] retained <address>.");
    }
}
