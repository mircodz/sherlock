using System;
using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Lists the library: workspaces (runs) and their snapshots, grouped by process.</summary>
public sealed class ListReplCommand : IReplCommand
{
    public string Name => "ls";
    public string Summary => "List workspaces and their snapshots.";
    public string Usage => "ls";
    public int MaxArgs => 0;
    public string Category => "Library";

    public ReplResult Execute(ReplContext context, string[] args)
    {
        IReadOnlyList<Session> sessions = context.Workspace.Store.Sessions;
        if (sessions.Count == 0)
        {
            Output.Info(context.Console, $"Nothing captured yet. Use [bold]run[/], [bold]collect[/], or [bold]import <file>[/].");
            return ReplResult.Success;
        }

        string? currentSnap = context.Workspace.CurrentEntry?.Id;
        string? currentWs = context.Workspace.CurrentSession?.Id;

        for (int sessionIndex = 0; sessionIndex < sessions.Count; sessionIndex++)
        {
            Session s = sessions[sessionIndex];
            if (sessionIndex > 0)
            {
                context.Console.WriteLine();
            }

            string kind = s.Kind.ToString().ToUpperInvariant();
            string when = s.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            long totalSize = s.Snapshots.Sum(snapshot => snapshot.TotalSizeBytes);
            string wsMarker = s.Id == currentWs ? $"[{Palette.Hot}]*[/]" : " ";
            context.Console.MarkupLine(
                $"{wsMarker} [bold]{s.Id}[/]  [{Palette.Muted}]{kind} · {when}[/]  [{Palette.Text}]{ByteSize.Format(totalSize)}[/]");

            if (s.Command is { } command)
            {
                context.Console.MarkupLine($"    {Markup.Escape(ShortCommand(command))}");
            }

            if (s.Processes.Count == 0)
            {
                context.Console.MarkupLine($"    [{Palette.Muted}]└─ no processes[/]");
                continue;
            }

            for (int processIndex = 0; processIndex < s.Processes.Count; processIndex++)
            {
                ProcessRecord proc = s.Processes[processIndex];
                bool lastProcess = processIndex == s.Processes.Count - 1;
                string processBranch = lastProcess ? "└─" : "├─";
                string name = proc.Name is { } n ? Markup.Escape(n) : "?";
                string role = proc.IsRoot ? "" : " · child";
                string profiled = proc.HasAllocations ? $" · [{Palette.Name}]allocations[/]" : "";
                context.Console.MarkupLine(
                    $"    [{Palette.Muted}]{processBranch}[/] [{Palette.Name}]{name}[/] [{Palette.Muted}]· pid {proc.Pid}{role}[/]{profiled}");

                if (proc.Snapshots.Count == 0)
                {
                    string emptyPrefix = lastProcess ? "       " : "    │  ";
                    context.Console.MarkupLine($"{emptyPrefix}[{Palette.Muted}]└─ no snapshots[/]");
                    continue;
                }

                for (int snapshotIndex = 0; snapshotIndex < proc.Snapshots.Count; snapshotIndex++)
                {
                    SnapshotEntry e = proc.Snapshots[snapshotIndex];
                    string snapshotPrefix = lastProcess ? "       " : "    │  ";
                    string snapshotBranch = snapshotIndex == proc.Snapshots.Count - 1 ? "└─" : "├─";
                    string marker = e.Id == currentSnap ? $"[{Palette.Hot}]*[/]" : " ";
                    string label = e.Label is { } l ? $"  [{Palette.Name}]{Markup.Escape(l)}[/]" : "";
                    string missing = e.Exists ? "" : $" [{Palette.Error}](missing)[/]";
                    string etime = e.CreatedAt.LocalDateTime.ToString("HH:mm");
                    string reason = e.Reason is { } r ? $"  [{Palette.Muted}]via {Markup.Escape(r)}[/]" : "";
                    context.Console.MarkupLine(
                        $"{snapshotPrefix}[{Palette.Muted}]{snapshotBranch}[/]{marker} [bold]{e.Id,-3}[/]  [{Palette.Muted}]{etime}[/]  " +
                        $"[{Palette.Text}]{ByteSize.Format(e.TotalSizeBytes),10}[/]  {Contents(e)}{reason}{label}{missing}");
                }
            }
        }

        context.Console.WriteLine();
        context.Console.MarkupLine($"[{Palette.Muted}]load <id> · label <id> <name> · rm <id|workspace>[/]");
        return ReplResult.Success;
    }

    private static string Contents(SnapshotEntry snapshot)
    {
        if (snapshot.HasCorrelation)
        {
            return $"heap + [{Palette.Name}]alloc[/] + [{Palette.Hot}]corr[/]";
        }
        if (snapshot.HasAllocations)
        {
            return $"heap + [{Palette.Name}]alloc[/]";
        }
        return $"[{Palette.Muted}]heap only[/]";
    }

    private static string ShortCommand(string command)
    {
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            int slash = Math.Max(parts[i].LastIndexOf('/'), parts[i].LastIndexOf('\\'));
            if (slash >= 0 && slash + 1 < parts[i].Length)
            {
                parts[i] = "…/" + parts[i][(slash + 1)..];
            }
        }
        return string.Join(' ', parts);
    }
}
