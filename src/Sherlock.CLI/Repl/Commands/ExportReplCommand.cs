using System;
using System.IO;
using System.Linq;
using Sherlock.CLI.Export;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Sherlock.Core.Profiling;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// Exports a view to a file for external tooling: the dominator tree as Graphviz DOT, or the
/// allocation profile as folded stacks (a flamegraph for Speedscope / flamegraph.pl).
/// </summary>
public sealed class ExportReplCommand : IReplCommand
{
    private const int DefaultDominatorNodes = 40;

    public string Name => "export";
    public string Summary => "Export a view to a file (dominators -> Graphviz .dot, allocations -> folded flamegraph).";
    public string Usage => "export <dominators [count] | allocations [--survived]> <file>";

    public void Execute(ReplContext context, string[] args)
    {
        Args.Require(args, 2, Usage);

        switch (args[0].ToLowerInvariant())
        {
            case "dominators" or "dom":
                ExportDominators(context, args);
                break;
            case "allocations" or "alloc":
                ExportAllocations(context, args);
                break;
            default:
                throw new DumpAnalysisException($"don't know how to export '{args[0]}'. Usage: {Usage}");
        }
    }

    private static void ExportDominators(ReplContext context, string[] args)
    {
        int count = args.Length >= 3 && int.TryParse(args[1], out int n) && n > 0 ? n : DefaultDominatorNodes;
        string file = FileArg(args);

        DominatorTree tree = context.Console.Status().Start("Building dominator tree…", _ => context.Snapshot.Dominators);
        Write(context, file, DominatorDot.Write(tree.BuildGraph(count)));
        context.Console.MarkupLineInterpolated($"[grey]render with[/] dot -Tsvg {Markup.Escape(file)} -o out.svg[grey].[/]");
    }

    private static void ExportAllocations(ReplContext context, string[] args)
    {
        bool survived = Array.IndexOf(args, "--survived") >= 0;
        string file = FileArg(args);

        AllocationProfile profile = context.Snapshot.Allocations
            ?? throw new DumpAnalysisException("this snapshot has no allocation profile (capture with `run --profile`/`--correlate`).");

        // .dot -> a pprof-style call graph (render with graphviz); anything else -> folded flamegraph.
        if (file.EndsWith(".dot", StringComparison.OrdinalIgnoreCase))
        {
            Write(context, file, AllocationDot.Write(profile));
            context.Console.MarkupLineInterpolated($"[grey]render with[/] dot -Tsvg {Markup.Escape(file)} -o out.svg[grey].[/]");
        }
        else
        {
            Write(context, file, FoldedStacks.Write(profile, survived));
            context.Console.MarkupLineInterpolated(
                $"[grey]open at[/] https://speedscope.app[grey], or[/] flamegraph.pl {Markup.Escape(file)} > out.svg[grey].[/]");
        }
    }

    /// <summary>The output path: the last argument that isn't the subcommand or a flag.</summary>
    private static string FileArg(string[] args)
    {
        string? file = args.Where((a, i) => i > 0 && !a.StartsWith("--", StringComparison.Ordinal)).LastOrDefault();
        return file ?? throw new DumpAnalysisException("no output file given.");
    }

    private static void Write(ReplContext context, string file, string content)
    {
        File.WriteAllText(file, content);
        long size = new FileInfo(file).Length;
        context.Console.MarkupLineInterpolated($"[green]✓[/] wrote [aqua]{Markup.Escape(file)}[/] [grey]({ByteSize.Format(size)})[/]");
    }
}
