using Sherlock.CLI.Rendering;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Lists the snapshot library.</summary>
public sealed class SnapshotsReplCommand : IReplCommand
{
    public string Name => "snapshots";
    public IReadOnlyList<string> Aliases => new[] { "snaps" };
    public string Summary => "List snapshots in the library.";
    public string Usage => "snapshots";
    public string Category => "Library";

    public void Execute(ReplContext context, string[] args)
    {
        IReadOnlyList<SnapshotEntry> entries = context.Workspace.Store.List();
        if (entries.Count == 0)
        {
            context.Console.MarkupLine("[grey]No snapshots yet. Use[/] collect[grey] or[/] import <file>[grey].[/]");
            return;
        }

        string? currentId = context.Workspace.CurrentEntry?.Id;

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(" ");
        table.AddColumn("[bold]Id[/]");
        table.AddColumn("[bold]Label[/]");
        table.AddColumn(new TableColumn("[bold]Size[/]").RightAligned());
        table.AddColumn("[bold]Origin[/]");
        table.AddColumn("[bold]Source[/]");
        table.AddColumn("[bold]When[/]");

        foreach (SnapshotEntry e in entries)
        {
            string marker = e.Id == currentId ? "[green]*[/]" : " ";
            string source = e.SourceProcess is { } proc
                ? $"{proc}{(e.SourcePid is int pid ? $" ({pid})" : "")}"
                : "-";
            string missing = e.Exists ? "" : " [red](missing)[/]";

            table.AddRow(
                marker,
                $"[bold]{e.Id}[/]",
                Markup.Escape(e.Label ?? "-"),
                ByteSize.Format(e.SizeBytes),
                e.Origin.ToString().ToLowerInvariant(),
                Markup.Escape(source) + missing,
                e.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm"));
        }

        context.Console.Write(table);
        context.Console.MarkupLine("[grey]load <id> to analyze · rm <id> to delete · label <id> <name>[/]");
    }
}
