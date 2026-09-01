using System;
using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl;

/// <summary>The interactive read-eval-print loop, holding one open session and dispatching typed lines.</summary>
public sealed class Repl(ReplCommandRegistry registry, ReplHistory history, IAnsiConsole console)
{
    private static readonly string[] ExitWords = ["exit", "quit", "q"];

    private ReplContext? _context;
    private Workspace? _workspace;
    private string? _lastCommand;

    private string Prompt => _workspace?.CurrentName is { } name ? $"sl[{name}]> " : "sl> ";

    /// <summary>Runs commands non-interactively, then returns. Used by <c>--exec</c> and scripts.</summary>
    public void RunBatch(Workspace workspace, IEnumerable<string> lines)
    {
        _workspace = workspace;
        _context = new ReplContext(workspace, console, RunLine);
        foreach (string line in lines)
        {
            console.MarkupLineInterpolated($"[#5AF78E]{Prompt}[/]{line}");
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

            string? line = LineEditor.ReadLine(Prompt, history, console);
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
                console.MarkupLineInterpolated($"[#5AF78E]{Prompt}[/][#808791]{line}[/]");
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
            Output.Error(console, $"Unknown command [bold]{name}[/]. Use [bold]help[/] for a list.");
            return true;
        }

        try
        {
            command.Execute(_context!, args);
        }
        catch (DumpAnalysisException ex)
        {
            Output.Error(console, $"{ex.Message}");
        }
        catch (Exception ex)
        {
            Output.Error(console, $"[bold]{command.Name}[/] failed: {ex.Message}");
        }

        return true;
    }

    private void PollTargets()
    {
        if (_workspace is null)
        {
            return;
        }

        foreach (Core.Store.Session session in _workspace.PollExitedAllocationProfiles())
        {
            Output.Success(console, $"Allocation profile captured for [bold]{session.Id}[/] [#808791]({session.Command})[/]");
        }

        foreach (TriggeredCaptureResult capture in _workspace.PollTriggeredSnapshots())
        {
            if (capture.Entry is { } entry)
            {
                string contents = entry.HasAllocations ? "heap + allocations" : "heap only";
                Output.Success(console, $"[bold]{capture.Probe}[/] fired · snapshot [bold]{entry.Id}[/] [#808791]({contents})[/]");
                if (capture.Error is not null)
                {
                    Output.Warning(console, $"{capture.Error}");
                }
            }
            else
            {
                Output.Error(console, $"[bold]{capture.Probe}[/] fired but capture failed: {capture.Error}");
            }
        }
    }

    private void PrintBanner(Workspace workspace)
    {
        if (workspace.Current is not null)
        {
            console.MarkupLineInterpolated($"[bold #5AF78E]sl[/] [#808791]·[/] [#00D7FF]{workspace.CurrentName}[/] [#808791]loaded[/]");
        }
        else
        {
            int count = workspace.Store.Sessions.Count;
            string workspaces = count == 1 ? "workspace" : "workspaces";
            console.MarkupLineInterpolated($"[bold #5AF78E]sl[/] [#808791]·[/] {count} {workspaces} [#808791]· no snapshot loaded[/]");
        }
        console.MarkupLine("[#808791]type `help` for commands · `exit` to quit[/]");
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
