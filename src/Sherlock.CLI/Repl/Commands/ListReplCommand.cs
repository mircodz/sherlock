using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Lists the library - workspaces (runs) and their snapshots, grouped by process.</summary>
public sealed class ListReplCommand : IReplCommand
{
    public string Name => "ls";
    public string Summary => "List workspaces and their snapshots.";
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
        string? currentWs = context.Workspace.CurrentSession?.Id;

        foreach (Session s in sessions)
        {
            // Workspace header: the run command (or collect/import source).
            string kind = s.Kind.ToString().ToLowerInvariant();
            string source = s.Command is { } c ? Markup.Escape(c) : "-";
            string when = s.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm");
            string wsMarker = s.Id == currentWs ? "[green]▸[/]" : " ";
            context.Console.MarkupLine(
                $"{wsMarker} [bold]{s.Id}[/] [grey]{kind} ·[/] {source}  [grey]{when}[/]");

            if (s.Processes.Count == 0)
            {
                context.Console.MarkupLine("      [grey](no processes)[/]");
                continue;
            }

            // A workspace spans one or more processes; each owns the snapshots taken from it.
            foreach (ProcessRecord proc in s.Processes)
            {
                string name = proc.Name is { } n ? Markup.Escape(n) : "?";
                string role = proc.IsRoot ? "" : " [grey]child[/]";
                string profiled = proc.HasAllocations ? "  [aqua]alloc[/]" : "";
                context.Console.MarkupLine(
                    $"    [aqua]{name}[/] [grey]({proc.Pid})[/]{role}{profiled}");

                if (proc.Snapshots.Count == 0)
                {
                    context.Console.MarkupLine("      [grey](no snapshots)[/]");
                    continue;
                }

                foreach (SnapshotEntry e in proc.Snapshots)
                {
                    string marker = e.Id == currentSnap ? "[green]*[/]" : " ";
                    string label = e.Label is { } l ? $"  [aqua]{Markup.Escape(l)}[/]" : "";
                    string missing = e.Exists ? "" : " [red](missing)[/]";
                    string etime = e.CreatedAt.LocalDateTime.ToString("HH:mm");
                    string reason = e.Reason is { } r ? $"  [grey]via {Markup.Escape(r)}[/]" : "";
                    context.Console.MarkupLine(
                        $"      {marker} [bold]{e.Id,-3}[/]  [green]{ByteSize.Format(e.SizeBytes),10}[/]  [grey]{etime}[/]  {Badges(e)}{reason}{label}{missing}");
                }
            }
        }

        context.Console.MarkupLine("[grey]load <id> · rm <id|workspace> · label <id> <name> · alloc[/]");
    }

    /// <summary>What data the snapshot bundle carries - heap always, plus allocations/correlation when profiled.</summary>
    private static string Badges(SnapshotEntry e)
    {
        var parts = new List<string>();
        if (e.HasAllocations)
        {
            parts.Add("[aqua]alloc[/]");
        }
        if (e.HasCorrelation)
        {
            parts.Add("[green]corr[/]");
        }
        return parts.Count > 0 ? string.Join(" ", parts) : "[grey]heap only[/]";
    }
}
