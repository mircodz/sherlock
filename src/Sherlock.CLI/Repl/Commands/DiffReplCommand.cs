using System;
using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Compares type counts and sizes between snapshots.</summary>
public sealed class DiffReplCommand : IReplCommand
{
    private const int DefaultLimit = 30;

    public string Name => "diff";
    public IReadOnlyList<string> Aliases => ["compare"];
    public string Summary => "Compare two snapshots by type: what grew and what's new (leak-finding).";
    public string Category => "Analysis";
    public string Usage => "diff <base> <target> [count]";
    public int MaxArgs => 3;

    public ReplResult Execute(ReplContext context, string[] args)
    {
        Args.Require(args, 2, Usage);
        int limit = Args.Count(args, 2, DefaultLimit, Usage);

        SnapshotEntry baseSnap = context.ResolveSnapshot(args[0]);
        SnapshotEntry targetSnap = context.ResolveSnapshot(args[1]);
        if (baseSnap.Path == targetSnap.Path)
        {
            context.Console.MarkupLine($"[{Palette.Warning}]Base and target are the same snapshot.[/]");
            return ReplResult.Success;
        }

        (Dictionary<string, HeapTypeStat> baseline, Dictionary<string, HeapTypeStat> target) =
            context.Console.Status().Start("Comparing snapshots…", _ =>
            {
                using Snapshot a = Snapshot.Open(baseSnap.Path);
                using Snapshot b = Snapshot.Open(targetSnap.Path);
                return (Index(a.Histogram), Index(b.Histogram));
            });

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
            context.Console.MarkupLineInterpolated($"[{Palette.Hot}]No differences[/] between {baseSnap.Id} and {targetSnap.Id}.");
            return ReplResult.Success;
        }

        List<(string Type, long DCount, long DBytes, bool IsNew)> grew =
            rows.Where(r => r.DBytes > 0).OrderByDescending(r => r.DBytes).ToList();

        context.Console.MarkupLineInterpolated(
            $"[{Palette.Muted}]diff[/] [bold]{baseSnap.Id}[/] [{Palette.Muted}]→[/] [bold]{targetSnap.Id}[/]  [{Palette.Muted}](growth = leak candidates)[/]");

        var table = Theme.Table(expand: true);
        table.AddColumn(new TableColumn("[bold]Δ bytes[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Δ count[/]").RightAligned());
        table.AddColumn("[bold]Type[/]");
        table.AddColumn("[bold]Namespace[/]");

        foreach ((string type, long dCount, long dBytes, bool isNew) in grew.Take(limit))
        {
            table.AddRow(
                $"[{Palette.Hot}]+{ByteSize.Format(dBytes)}[/]",
                $"+{Counts.Format(dCount)}",
                $"{Styled.Type(type)}{(isNew ? $" [{Palette.Hot}](new)[/]" : "")}",
                Styled.Namespace(type));
        }

        context.Console.Write(table);

        long netBytes = rows.Sum(r => r.DBytes);
        long grewBytes = grew.Sum(r => r.DBytes);
        int shrank = rows.Count(r => r.DBytes < 0);
        context.Console.MarkupLineInterpolated(
            $"[{Palette.Muted}]{grew.Count} types grew ([/][{Palette.Hot}]+{ByteSize.Format(grewBytes)}[/][{Palette.Muted}]), {shrank} shrank. Net {(netBytes >= 0 ? "+" : "-")}[/][bold]{ByteSize.Format(Math.Abs(netBytes))}[/][{Palette.Muted}].[/]");
        return ReplResult.Success;
    }

    private static Dictionary<string, HeapTypeStat> Index(IReadOnlyList<HeapTypeStat> stats) =>
        stats.ToDictionary(s => s.TypeName, s => s);
}
