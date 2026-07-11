using System;
using System.Collections.Generic;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl;

/// <summary>
/// The interactive read-eval-print loop. Holds one open <see cref="DumpSession"/>
/// for the lifetime of the session and dispatches typed lines to commands.
/// </summary>
public sealed class Repl(ReplCommandRegistry registry, ReplHistory history, IAnsiConsole console)
{
    private static readonly string[] ExitWords = ["exit", "quit", "q"];

    private ReplContext? _context;
    private Workspace? _workspace;
    private string? _lastCommand;

    private string Prompt => _workspace?.CurrentName is { } name ? $"sherlock[{name}]> " : "sherlock> ";

    /// <summary>Runs commands non-interactively, then returns. Used by <c>--exec</c> and scripts.</summary>
    public void RunBatch(Workspace workspace, IEnumerable<string> lines)
    {
        _workspace = workspace;
        _context = new ReplContext(workspace, console, RunLine);
        foreach (string line in lines)
        {
            console.MarkupLineInterpolated($"[green]{Prompt}[/]{line}");
            if (!RunLine(line))
            {
                return;
            }
        }
    }

    /// <summary>Runs the interactive loop until the user exits or input ends.</summary>
    public void RunInteractive(Workspace workspace)
    {
        _workspace = workspace;
        _context = new ReplContext(workspace, console, RunLine);
        PrintBanner(workspace);

        while (true)
        {
            PollTargets();

            string? line = LineEditor.ReadLine(Prompt, history);
            if (line is null) // EOF (Ctrl-D)
            {
                console.WriteLine();
                return;
            }

            line = line.Trim();

            // Empty Enter repeats the previous command (gdb-style).
            if (line.Length == 0)
            {
                if (_lastCommand is null)
                {
                    continue;
                }

                line = _lastCommand;
                console.MarkupLineInterpolated($"[grey]{Prompt}{line}[/]");
            }
            else
            {
                history.Add(line);
                _lastCommand = line;
            }

            if (!RunLine(line))
            {
                return;
            }
        }
    }

    /// <summary>Dispatches one input line. Returns false when the loop should stop.</summary>
    private bool RunLine(string line)
    {
        string[] tokens = Tokenize(line);
        if (tokens.Length == 0)
        {
            return true;
        }

        string name = tokens[0];
        string[] args = tokens[1..];

        if (ExitWords.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        IReplCommand? command = registry.Resolve(name);
        if (command is null)
        {
            console.MarkupLineInterpolated($"[red]unknown command:[/] {name}. Type [bold]help[/] for a list.");
            return true;
        }

        try
        {
            command.Execute(_context!, args);
        }
        catch (DumpAnalysisException ex)
        {
            console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
        }
        catch (Exception ex)
        {
            // Keep the session alive: one bad command shouldn't end the REPL.
            console.MarkupLineInterpolated($"[red]{command.Name} failed:[/] {ex.Message}");
        }

        return true;
    }

    /// <summary>Auto-imports crash dumps from run-targets that have exited.</summary>
    private void PollTargets()
    {
        if (_workspace is null)
        {
            return;
        }

        foreach (Sherlock.Core.Store.SnapshotEntry entry in _workspace.PollExitedCrashDumps())
        {
            console.MarkupLineInterpolated(
                $"[yellow]· crash dump captured as[/] [bold]{entry.Id}[/] [grey]— load {entry.Id} to analyze[/]");
        }

        foreach (Sherlock.Core.Store.Session session in _workspace.PollExitedAllocationProfiles())
        {
            console.MarkupLineInterpolated(
                $"[yellow]· allocation profile captured for session[/] [bold]{session.Id}[/] [grey]({session.Command})[/]");
        }

        foreach ((Sherlock.Core.Store.SnapshotEntry entry, string probe) in _workspace.PollProbeSnapshots())
        {
            console.MarkupLineInterpolated(
                $"[yellow]●[/] [bold]{probe}[/] [yellow]fired — heap snapshot[/] [bold]{entry.Id}[/] [grey]captured; load {entry.Id} to inspect[/]");
        }
    }

    private void PrintBanner(Workspace workspace)
    {
        if (workspace.Current is not null)
        {
            console.MarkupLineInterpolated($"[bold]Sherlock[/] — loaded [aqua]{workspace.CurrentName}[/]");
        }
        else
        {
            int count = workspace.Store.Sessions.Count;
            console.MarkupLineInterpolated($"[bold]Sherlock[/] — no snapshot loaded ([aqua]{count}[/] sessions in library)");
            console.MarkupLine("[grey]Use[/] snapshots[grey],[/] load <id>[grey],[/] collect[grey], or[/] import <file>[grey].[/]");
        }
        console.MarkupLine("Type [bold]help[/] for commands, [bold]exit[/] to quit.");
        console.WriteLine();
    }

    /// <summary>Splits a line into tokens, treating double-quoted spans as one token.</summary>
    private static string[] Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens.ToArray();
    }
}
