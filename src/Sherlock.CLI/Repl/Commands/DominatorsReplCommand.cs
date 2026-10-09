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
    public int MaxArgs => 1;

    public ReplResult Execute(ReplContext context, string[] args)
    {
        int limit = Args.Count(args, 0, DefaultLimit, Usage);

        DominatorTree tree = context.Console.Status()
            .Start("Building dominator tree…", _ => context.Snapshot.GetDominatorTree(context.Cancellation));

        IReadOnlyList<DominatorNode> top = tree.TopDominators(limit);
        ulong total = tree.TotalReachableBytes;

        var table = Theme.Table(expand: true);
        table.AddColumn("[bold]Address[/]");
        table.AddColumn(new TableColumn("[bold]Retained[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]%[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Shallow[/]").RightAligned());
        table.AddColumn("[bold]Type[/]");

        foreach (DominatorNode node in top)
        {
            double pct = total == 0 ? 0 : 100.0 * node.RetainedSize / total;
            table.AddRow(
                $"[{Palette.Address}]0x{node.Address:x}[/]",
                $"[bold {Palette.Hot}]{ByteSize.Format((long)node.RetainedSize)}[/]",
                Counts.Percent(pct),
                ByteSize.Format((long)node.OwnSize),
                Styled.Type(node.TypeName));
        }

        context.Console.Write(table);
        context.Console.MarkupLine(
            $"[{Palette.Muted}]{Counts.Format(tree.ObjectCount)} reachable objects,[/] [bold {Palette.Hot}]{ByteSize.Format((long)total)}[/] [{Palette.Muted}]retained from roots. " +
            $"Drill in with[/] retained <address>.");
        return ReplResult.Success;
    }
}
