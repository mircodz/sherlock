using System;
using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// Compares two snapshots by type — what grew, what's new — the core leak-finding workflow 
/// </summary>
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
        if (args.Length < 2)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage}");
            return;
        }

        int limit = args.Length > 2 && int.TryParse(args[2], out int n) && n > 0 ? n : DefaultLimit;

        if (!Resolve(context, args[0], out string basePath, out string baseId) ||
            !Resolve(context, args[1], out string targetPath, out string targetId))
        {
            return;
        }
        if (basePath == targetPath)
        {
            context.Console.MarkupLine("[yellow]Base and target are the same snapshot.[/]");
            return;
        }

        (Dictionary<string, HeapTypeStat> baseline, Dictionary<string, HeapTypeStat> target) =
            context.Console.Status().Start("Comparing snapshots…", _ =>
            {
                using DumpSession a = DumpSession.Open(basePath);
                using DumpSession b = DumpSession.Open(targetPath);
                return (Index(a.GetHistogram()), Index(b.GetHistogram()));
            });

        // Per-type deltas across the union of types in both snapshots.
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
            context.Console.MarkupLineInterpolated($"[green]No differences[/] between {baseId} and {targetId}.");
            return;
        }

        List<(string Type, long DCount, long DBytes, bool IsNew)> grew =
            rows.Where(r => r.DBytes > 0).OrderByDescending(r => r.DBytes).ToList();

        context.Console.MarkupLineInterpolated(
            $"[grey]diff[/] [bold]{baseId}[/] [grey]→[/] [bold]{targetId}[/]  [grey](growth = leak candidates)[/]");

        var table = new Table().Border(TableBorder.Square).Expand();
        table.AddColumn(new TableColumn("[bold]Δ bytes[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Δ count[/]").RightAligned());
        table.AddColumn("[bold]Type[/]");

        foreach ((string type, long dCount, long dBytes, bool isNew) in grew.Take(limit))
        {
            table.AddRow(
                $"[red]+{ByteSize.Format(dBytes)}[/]",
                $"+{dCount:N0}",
                $"[aqua]{Markup.Escape(TypeNames.Short(type))}[/]{(isNew ? " [yellow](new)[/]" : "")}");
        }

        context.Console.Write(table);

        long netBytes = rows.Sum(r => r.DBytes);
        long grewBytes = grew.Sum(r => r.DBytes);
        int shrank = rows.Count(r => r.DBytes < 0);
        context.Console.MarkupLineInterpolated(
            $"[grey]{grew.Count} types grew ([/][red]+{ByteSize.Format(grewBytes)}[/][grey]), {shrank} shrank. Net {(netBytes >= 0 ? "+" : "-")}[/][bold]{ByteSize.Format(Math.Abs(netBytes))}[/][grey].[/]");
    }

    private static Dictionary<string, HeapTypeStat> Index(IReadOnlyList<HeapTypeStat> stats) =>
        stats.ToDictionary(s => s.TypeName, s => s);

    private static bool Resolve(ReplContext context, string idOrLabel, out string path, out string id)
    {
        path = string.Empty;
        id = idOrLabel;
        if (context.Workspace.Store.FindSnapshot(idOrLabel) is not (_, { } snap))
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] no snapshot '{idOrLabel}'. See [bold]snapshots[/].");
            return false;
        }
        if (!snap.Exists)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] snapshot '{idOrLabel}' file is missing.");
            return false;
        }

        path = snap.Path;
        id = snap.Id;
        return true;
    }
}
