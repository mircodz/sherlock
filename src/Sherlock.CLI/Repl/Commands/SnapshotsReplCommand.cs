using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Lists the library, grouped by session (a run, or a one-off collect/import).</summary>
public sealed class SnapshotsReplCommand : IReplCommand
{
    public string Name => "snapshots";
    public IReadOnlyList<string> Aliases => ["snaps", "ls"];
    public string Summary => "List sessions and their snapshots.";
    public string Usage => "snapshots";
    public string Category => "Library";

    public void Execute(ReplContext context, string[] args)
    {
        IReadOnlyList<Session> sessions = context.Workspace.Store.Sessions;
        if (sessions.Count == 0)
        {
            context.Console.MarkupLine("[grey]Nothing yet. Use[/] run[grey],[/] collect[grey], or[/] import <file>[grey].[/]");
            return;
        }

        string? currentSnap = context.Workspace.CurrentEntry?.Id;

        foreach (Session s in sessions)
        {
            string source = s.SourceProcess is { } p
                ? $"{p}{(s.SourcePid is int pid ? $" ({pid})" : "")}"
                : "-";
            string profiled = s.HasAllocations ? "  [aqua]profiled[/]" : "";
            string when = s.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm");
            // MarkupLine parses the whole string as markup; escape only dynamic values.
            context.Console.MarkupLine(
                $"[bold]{s.Id}[/] [grey]{s.Kind.ToString().ToLowerInvariant()}[/]  {Markup.Escape(source)}{profiled}  [grey]{when}[/]");

            foreach (SnapshotEntry e in s.Snapshots)
            {
                string marker = e.Id == currentSnap ? "[green]*[/]" : " ";
                string label = e.Label is { } l ? $"  [aqua]{Markup.Escape(l)}[/]" : "";
                string missing = e.Exists ? "" : "  [red](missing)[/]";
                context.Console.MarkupLine(
                    $"  {marker} [bold]{e.Id}[/]  {Markup.Escape(ByteSize.Format(e.SizeBytes))}{label}{missing}");
            }

            if (s.Snapshots.Count == 0 && !s.HasAllocations)
            {
                context.Console.MarkupLine("    [grey](no snapshots)[/]");
            }
        }

        context.Console.MarkupLine("[grey]load <id> · rm <id|session> · label <id> <name> · alloc[/]");
    }
}
