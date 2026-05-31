using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// A lightweight "leak suspects" report (à la Eclipse MAT): top dominators that
/// each retain more than a threshold percentage of the reachable heap.
/// </summary>
public sealed class LeaksReplCommand : IReplCommand
{
    private const double DefaultThresholdPercent = 5.0;

    public string Name => "leaks";
    public IReadOnlyList<string> Aliases => new[] { "suspects" };
    public string Summary => "Leak suspects: objects retaining more than N% of the heap (default 5%).";
    public string Usage => "leaks [min-percent]";

    public void Execute(ReplContext context, string[] args)
    {
        double threshold = DefaultThresholdPercent;
        if (args.Length > 0 && double.TryParse(args[0], out double parsed))
            threshold = parsed;

        DominatorTree tree = context.Console.Status()
            .Start("Building dominator tree…", _ => context.Session.GetDominatorTree());

        ulong total = tree.TotalReachableBytes;
        if (total == 0)
        {
            context.Console.MarkupLine("[yellow]Heap is empty.[/]");
            return;
        }

        var suspects = tree.TopDominators(100)
            .Where(n => 100.0 * n.RetainedSize / total >= threshold)
            .ToList();

        if (suspects.Count == 0)
        {
            context.Console.MarkupLineInterpolated(
                $"[green]No single object retains ≥ {threshold:0.#}% of the heap.[/] No obvious leak suspect.");
            return;
        }

        context.Console.MarkupLineInterpolated($"[bold]{suspects.Count} leak suspect(s)[/] retaining ≥ {threshold:0.#}% of {ByteSize.Format((long)total)}:");
        context.Console.WriteLine();

        const int width = 50;
        foreach (DominatorNode node in suspects)
        {
            double pct = 100.0 * node.RetainedSize / total;
            int filled = Math.Clamp((int)Math.Round(pct / 100 * width), 0, width);
            string bar = new string('█', filled) + new string('░', width - filled);

            context.Console.MarkupLineInterpolated(
                $"[red]●[/] [bold]{ByteSize.Format((long)node.RetainedSize)}[/] ([bold]{pct:0.0}%[/]) — {node.TypeName} [grey]@ 0x{node.Address:x}[/]");
            context.Console.MarkupLineInterpolated($"  [red]{bar}[/]");
            context.Console.WriteLine();
        }
    }
}
