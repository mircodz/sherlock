using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
    public ReplResult RunBatch(Workspace workspace, IEnumerable<string> lines, CancellationToken cancellation = default)
    {
        _workspace = workspace;
        _context = new ReplContext(workspace, console, RunLine, cancellation);
        var result = ReplResult.Success;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (string line in lines)
            {
                console.MarkupLineInterpolated($"[#5AF78E]{Prompt}[/]{line}");
                result |= RunLine(line);
                if ((result & (ReplResult.Quit | ReplResult.Cancelled)) != 0)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return result | ReplResult.Cancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Output.Error(console, $"{ex.Message}");
            return result | ReplResult.Failure;
        }
        return cancellation.IsCancellationRequested ? result | ReplResult.Cancelled : result;
    }

    /// <summary>Runs the interactive loop until the user exits or input ends.</summary>
    public ReplResult RunInteractive(Workspace workspace, CancellationToken cancellation = default) =>
        RunInteractive(workspace, prompt => LineEditor.ReadLine(prompt, history, console, cancellation), cancellation);

    internal ReplResult RunInteractive(Workspace workspace, Func<string, string?> readLine, CancellationToken cancellation = default)
    {
        _workspace = workspace;
        _context = new ReplContext(workspace, console, RunLine, cancellation);
        PrintBanner(workspace);
        var result = ReplResult.Success;

        while (true)
        {
            if (cancellation.IsCancellationRequested)
            {
                return result | ReplResult.Cancelled;
            }
            try
            {
                result |= PollTargets();
            }
            catch (OperationCanceledException)
            {
                result |= ReplResult.Cancelled;
                continue;
            }
            catch (Exception ex)
            {
                Output.Error(console, $"{ex.Message}");
                result |= ReplResult.Failure;
            }
            string? line;
            try
            {
                line = readLine(Prompt);
            }
            catch (OperationCanceledException)
            {
                result |= ReplResult.Cancelled;
                continue;
            }
            catch (Exception ex)
            {
                Output.Error(console, $"{ex.Message}");
                return result | ReplResult.Failure;
            }
            if (line is null) // EOF (Ctrl-D)
            {
                console.WriteLine();
                return result | ReplResult.Quit;
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

            ReplResult commandResult = RunLine(line);
            result |= commandResult;
            if ((commandResult & ReplResult.Quit) != 0)
            {
                return result;
            }
        }
    }

    /// <summary>Dispatches one input line without losing failures or cancellation.</summary>
    private ReplResult RunLine(string line)
    {
        IReplCommand? command = null;
        try
        {
            _context!.Cancellation.ThrowIfCancellationRequested();
            string[] tokens = Tokenize(line);
            if (tokens.Length == 0)
            {
                return ReplResult.Success;
            }
            string name = tokens[0];
            string[] args = tokens[1..];
            if (ExitWords.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return args.Length == 0 ? ReplResult.Quit : throw new DumpAnalysisException($"usage: {name}");
            }
            command = registry.Resolve(name);
            if (command is null)
            {
                Output.Error(console, $"Unknown command [bold]{name}[/]. Use [bold]help[/] for a list.");
                return ReplResult.Failure;
            }
            ReplResult result = command.Execute(_context, args);
            return _context.Cancellation.IsCancellationRequested ? result | ReplResult.Cancelled : result;
        }
        catch (OperationCanceledException)
        {
            Output.Warning(console, $"Command cancelled.");
            return ReplResult.Cancelled;
        }
        catch (DumpAnalysisException ex)
        {
            Output.Error(console, $"{ex.Message}");
            return ReplResult.Failure;
        }
        catch (Exception ex)
        {
            Output.Error(console, $"[bold]{command?.Name ?? "command"}[/] failed: {ex.Message}");
            return ReplResult.Failure;
        }
    }

    private ReplResult PollTargets()
    {
        if (_workspace is null)
        {
            return ReplResult.Success;
        }

        var result = ReplResult.Success;
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
                    result |= ReplResult.Failure;
                }
            }
            else
            {
                Output.Error(console, $"[bold]{capture.Probe}[/] fired but capture failed: {capture.Error}");
                result |= ReplResult.Failure;
            }
        }
        return result;
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
        bool started = false;

        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (started)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (inQuotes)
        {
            throw new DumpAnalysisException("Unterminated double quote.");
        }
        if (started)
        {
            tokens.Add(current.ToString());
        }

        return tokens.ToArray();
    }
}
