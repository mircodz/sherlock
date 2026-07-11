using System;
using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// Runs heuristics over the snapshot and reports suspected memory issues (big retained graphs,
/// duplicate strings, fragmentation), each pointing at the command that drills in.
/// </summary>
public sealed class InspectReplCommand : IReplCommand
{
    public string Name => "inspect";
    public IReadOnlyList<string> Aliases => ["leaks", "doctor"];
    public string Summary => "Heuristic scan for memory issues (retainers, dup strings, fragmentation, growth).";
    public string Usage => "inspect";

    private enum Severity { High, Warn, Info }

    private readonly record struct Finding(Severity Severity, string Title, string Hint);

    public void Execute(ReplContext context, string[] args)
    {
        var findings = new List<Finding>();

        context.Console.Status().Start("Inspecting heap…", _ =>
        {
            IReadOnlyList<HeapTypeStat> stats = context.Snapshot.Histogram;
            long heapTotal = stats.Sum(s => (long)s.TotalSize);

            LargeRetainers(context.Snapshot, findings);
            DuplicateStrings(context.Snapshot, heapTotal, findings);
            Fragmentation(stats, heapTotal, findings);
            GrowthSuspects(stats, heapTotal, findings);
        });

        if (findings.Count == 0)
        {
            context.Console.MarkupLine("[green]No obvious issues found.[/] [grey]Heap looks healthy by the current heuristics.[/]");
            return;
        }

        foreach (Finding f in findings.OrderBy(f => f.Severity))
        {
            (string icon, string colour) = f.Severity switch
            {
                Severity.High => ("●", "red"),
                Severity.Warn => ("●", "yellow"),
                _ => ("●", "aqua"),
            };
            // Titles/hints already contain markup (with dynamic parts pre-escaped), so use
            // MarkupLine, not MarkupLineInterpolated (which would escape the markup holes).
            context.Console.MarkupLine($"[{colour}]{icon}[/] {f.Title}");
            context.Console.MarkupLine($"   [grey]{f.Hint}[/]");
        }
    }

    /// <summary>Biggest retained graphs - where memory concentrates and where a leak hides.</summary>
    private static void LargeRetainers(Snapshot snapshot, List<Finding> findings)
    {
        DominatorTree tree = snapshot.Dominators;
        ulong total = tree.TotalReachableBytes;
        if (total == 0)
        {
            return;
        }

        // Report only the single biggest retained graph. The top dominators tend to be a nested
        // chain (Object[] > Registry > List > ...) that all retain about the same bytes, so listing
        // them all is noise. `dominators` shows the full breakdown.
        DominatorNode? node = tree.TopDominators(1).FirstOrDefault();
        if (node is null)
        {
            return;
        }

        double pct = 100.0 * node.RetainedSize / total;
        if (pct < 10)
        {
            return;
        }

        string kind = IsCollection(node.TypeName) ? "collection " : "";
        Severity sev = pct >= 50 ? Severity.High : Severity.Warn;
        findings.Add(new Finding(sev,
            $"{Short(node.TypeName)} {kind}retains [bold green]{ByteSize.Format((long)node.RetainedSize)}[/] ({pct:0}% of reachable heap)",
            $"0x{node.Address:x} — [bold]gcroot[/] why it's held, [bold]retained[/] what it holds, [bold]whoalloc[/] where it came from. [bold]dominators[/] for the full list."));
    }

    private static void DuplicateStrings(Snapshot snapshot, long heapTotal, List<Finding> findings)
    {
        IReadOnlyList<DuplicateString> dups = snapshot.DuplicateStrings(limit: 100);
        long wasted = dups.Sum(d => (long)d.WastedBytes);
        if (wasted < 64 * 1024 && (heapTotal == 0 || (double)wasted / heapTotal < 0.02))
        {
            return;
        }

        string top = dups.Count > 0 ? Preview(dups[0].Value) : "";
        findings.Add(new Finding(Severity.Warn,
            $"Duplicate strings waste [bold green]{ByteSize.Format(wasted)}[/] across {dups.Count} values",
            $"e.g. \"{top}\" ×{(dups.Count > 0 ? dups[0].Count : 0)}. Intern/dedupe. See [bold]strings[/]."));
    }

    private static void Fragmentation(IReadOnlyList<HeapTypeStat> stats, long heapTotal, List<Finding> findings)
    {
        HeapTypeStat? free = stats.FirstOrDefault(s => s.TypeName == "Free");
        if (free is null || heapTotal == 0)
        {
            return;
        }

        double pct = 100.0 * free.TotalSize / heapTotal;
        if (pct < 25)
        {
            return;
        }

        findings.Add(new Finding(Severity.Warn,
            $"Heap fragmentation: [bold green]{ByteSize.Format((long)free.TotalSize)}[/] free ({pct:0}% of heap)",
            "High free-space ratio — fragmentation, or a large collection that just happened. Check [bold]segments[/]."));
    }

    /// <summary>A user type with a large instance count - a candidate for unbounded growth.</summary>
    private static void GrowthSuspects(IReadOnlyList<HeapTypeStat> stats, long heapTotal, List<Finding> findings)
    {
        HeapTypeStat? suspect = stats
            .Where(s => s.Count >= 10_000 && !IsFramework(s.TypeName) && s.TypeName != "Free")
            .OrderByDescending(s => s.TotalSize)
            .FirstOrDefault();
        if (suspect is null)
        {
            return;
        }

        findings.Add(new Finding(Severity.Info,
            $"[bold]{suspect.Count:N0}[/] instances of {Short(suspect.TypeName)} ([bold green]{ByteSize.Format((long)suspect.TotalSize)}[/])",
            "Large population — check for unbounded growth (a cache/list that never shrinks). [bold]objects[/] to list them."));
    }

    private static bool IsCollection(string type) =>
        type.Contains("List<") || type.Contains("Dictionary<") || type.Contains("HashSet<") ||
        type.Contains("Queue<") || type.Contains("Stack<") || type.Contains("[]");

    private static bool IsFramework(string type) =>
        type.StartsWith("System.", StringComparison.Ordinal) ||
        type.StartsWith("Microsoft.", StringComparison.Ordinal);

    // Markup-safe short type name / value preview via the shared Rendering helpers.
    private static string Short(string type) => Markup.Escape(TypeNames.Short(type));

    private static string Preview(string value) => Markup.Escape(TextUtil.Preview(value, 48));
}
