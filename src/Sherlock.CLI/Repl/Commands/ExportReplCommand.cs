using System.Collections.Generic;
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

/// <summary>Exports a view to a file: the dominator tree as Graphviz DOT, or the allocation profile as folded stacks.</summary>
public sealed class ExportReplCommand : IReplCommand
{
    private const int DefaultDominatorNodes = 40;

    public string Name => "export";
    public string Summary => "Export dominators (.dot) or allocations (folded flame graph) to a file.";
    private const string UsageText = "export <dominators [count] | allocations [--survived]> <file>";
    public string Usage => UsageText;
    public int MaxArgs => 3;
    public IReadOnlyList<string>? Options => ["--survived"];

    public ReplResult Execute(ReplContext context, string[] args)
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
        return ReplResult.Success;
    }

    private static void ExportDominators(ReplContext context, string[] args)
    {
        int count = args.Length >= 3 ? Args.Count(args, 1, DefaultDominatorNodes, UsageText) : DefaultDominatorNodes;
        string file = FileArg(args);

        DominatorTree tree = context.Console.Status().Start("Building dominator tree…", _ => context.Snapshot.GetDominatorTree(context.Cancellation));
        Write(context, file, DominatorDot.Write(tree.BuildGraph(count)));
        context.Console.MarkupLineInterpolated($"[{Palette.Muted}]render with[/] dot -Tsvg {Markup.Escape(file)} -o out.svg[{Palette.Muted}].[/]");
    }

    private static void ExportAllocations(ReplContext context, string[] args)
    {
        bool survived = Array.IndexOf(args, "--survived") >= 0;
        string file = FileArg(args);

        AllocationProfile profile = context.Snapshot.Allocations
            ?? throw new DumpAnalysisException("this snapshot has no allocation profile (capture with `run --profile`/`--correlate`).");

        // .dot -> pprof-style call graph (graphviz); anything else -> folded flamegraph.
        if (file.EndsWith(".dot", StringComparison.OrdinalIgnoreCase))
        {
            if (survived)
            {
                throw new DumpAnalysisException("--survived applies to folded stacks, not .dot call graphs.");
            }
            Write(context, file, AllocationDot.Write(profile));
            context.Console.MarkupLineInterpolated($"[{Palette.Muted}]render with[/] dot -Tsvg {Markup.Escape(file)} -o out.svg[{Palette.Muted}].[/]");
        }
        else
        {
            Write(context, file, FoldedStacks.Write(profile, survived));
            context.Console.MarkupLineInterpolated(
                $"[{Palette.Muted}]open at[/] https://speedscope.app[{Palette.Muted}], or[/] flamegraph.pl {Markup.Escape(file)} > out.svg[{Palette.Muted}].[/]");
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
        context.Console.MarkupLineInterpolated($"[{Palette.Hot}]✓[/] wrote [{Palette.Name}]{Markup.Escape(file)}[/] [{Palette.Muted}]({ByteSize.Format(size)})[/]");
    }
}
