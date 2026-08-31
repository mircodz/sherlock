using System;
using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Compares two snapshots by type - what grew, what's new. The core leak-finding workflow.</summary>
public sealed class DiffReplCommand : IReplCommand
{
    private const int DefaultLimit = 30;

    public string Name => "diff";
    public IReadOnlyList<string> Aliases => ["compare"];
    public string Summary => "Compare two snapshots by type: what grew and what's new (leak-finding).";
    public string Category => "Analysis";
    public string Usage => "diff <base> <target> [count]";

    public void Execute(ReplContext context, string[] args)
    {
        Args.Require(args, 2, Usage);
        int limit = Args.Limit(args, 2, DefaultLimit);

        SnapshotEntry baseSnap = context.ResolveSnapshot(args[0]);
        SnapshotEntry targetSnap = context.ResolveSnapshot(args[1]);
        if (baseSnap.Path == targetSnap.Path)
        {
            context.Console.MarkupLine("[#FFAF00]Base and target are the same snapshot.[/]");
            return;
        }

        (Dictionary<string, HeapTypeStat> baseline, Dictionary<string, HeapTypeStat> target) =
            context.Console.Status().Start("Comparing snapshots…", _ =>
            {
                using Snapshot a = Snapshot.Open(baseSnap.Path);
                using Snapshot b = Snapshot.Open(targetSnap.Path);
                return (Index(a.Histogram), Index(b.Histogram));
            });

        // Per-type deltas over the union of types in both snapshots.
        var rows = new List<(string Type, long DCount, long DBytes, bool IsNew)>();
        foreach (string type in baseline.Keys.Union(target.Keys))
        {
            baseline.TryGetValue(type, out HeapTypeStat? a);
            target.TryGetValue(type, out HeapTypeStat? b);
            long dCount = (b?.Count ?? 0) - (a?.Count ?? 0);
            long dBytes = (long)(b?.TotalSize ?? 0) - (long)(a?.TotalSize ?? 0);
            if (dCount == 0 && dBytes == 0)
            {
                continue;
            }
            rows.Add((type, dCount, dBytes, a is null));
        }

        if (rows.Count == 0)
        {
            context.Console.MarkupLineInterpolated($"[#AFFF00]No differences[/] between {baseSnap.Id} and {targetSnap.Id}.");
            return;
        }

        List<(string Type, long DCount, long DBytes, bool IsNew)> grew =
            rows.Where(r => r.DBytes > 0).OrderByDescending(r => r.DBytes).ToList();

        context.Console.MarkupLineInterpolated(
            $"[#808791]diff[/] [bold]{baseSnap.Id}[/] [#808791]→[/] [bold]{targetSnap.Id}[/]  [#808791](growth = leak candidates)[/]");

        var table = Theme.Table(expand: true);
        table.AddColumn(new TableColumn("[bold]Δ bytes[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Δ count[/]").RightAligned());
        table.AddColumn("[bold]Type[/]");

        foreach ((string type, long dCount, long dBytes, bool isNew) in grew.Take(limit))
        {
            table.AddRow(
                $"[#AFFF00]+{ByteSize.Format(dBytes)}[/]",
                $"+{Counts.Format(dCount)}",
                $"[#00D7FF]{Markup.Escape(TypeNames.Short(type))}[/]{(isNew ? " [#AFFF00](new)[/]" : "")}");
        }

        context.Console.Write(table);

        long netBytes = rows.Sum(r => r.DBytes);
        long grewBytes = grew.Sum(r => r.DBytes);
        int shrank = rows.Count(r => r.DBytes < 0);
        context.Console.MarkupLineInterpolated(
            $"[#808791]{grew.Count} types grew ([/][#AFFF00]+{ByteSize.Format(grewBytes)}[/][#808791]), {shrank} shrank. Net {(netBytes >= 0 ? "+" : "-")}[/][bold]{ByteSize.Format(Math.Abs(netBytes))}[/][#808791].[/]");
    }

    private static Dictionary<string, HeapTypeStat> Index(IReadOnlyList<HeapTypeStat> stats) =>
        stats.ToDictionary(s => s.TypeName, s => s);
}
