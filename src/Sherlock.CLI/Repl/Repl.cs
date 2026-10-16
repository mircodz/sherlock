using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl;

/// <summary>Dispatches interactive and batched commands against a workspace.</summary>
public sealed class Repl(ReplCommandRegistry registry, ReplHistory history, IAnsiConsole console)
{
    private static readonly string[] ExitWords = ["exit", "quit", "q"];

    private ReplContext? _context;
    private string? _lastCommand;

    private string Prompt => _context?.Workspace.CurrentName is { } name ? $"sl[{name}]> " : "sl> ";

    /// <summary>Continues after failures; quit and cancellation stop the batch.</summary>
    public ReplResult RunBatch(Workspace workspace, IEnumerable<string> lines, CancellationToken cancellation = default)
    {
        _context = new ReplContext(workspace, console, RunLine, cancellation);
        var result = ReplResult.Success;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (string line in lines)
            {
                console.MarkupLineInterpolated($"[{Palette.Hot}]{Prompt}[/]{line}");
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

    public ReplResult RunInteractive(Workspace workspace, CancellationToken cancellation = default) =>
        RunInteractive(workspace, prompt => LineEditor.ReadLine(prompt, history, console, cancellation), cancellation);

    internal ReplResult RunInteractive(Workspace workspace, Func<string, string?> readLine, CancellationToken cancellation = default)
    {
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

            // Empty input repeats the previous command.
            if (line.Length == 0)
            {
                if (_lastCommand is null)
                {
                    continue;
                }

                line = _lastCommand;
                console.MarkupLineInterpolated($"[{Palette.Hot}]{Prompt}[/][{Palette.Muted}]{line}[/]");
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
            Validate(command, args);
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

    private static void Validate(IReplCommand command, string[] args)
    {
        if (command.Options is { } options)
        {
            foreach (string arg in args)
            {
                if (arg == "--")
                {
                    break; // everything after "--" is an operand
                }
                if (arg.StartsWith("--", StringComparison.Ordinal) && !System.Linq.Enumerable.Contains(options, arg))
                {
                    throw new DumpAnalysisException($"Unknown option {arg}. usage: {command.Usage}");
                }
            }
        }
        if (args.Length > command.MaxArgs)
        {
            throw new DumpAnalysisException(command.MaxArgs == 0
                ? $"{command.Name} takes no arguments."
                : $"Too many arguments. usage: {command.Usage}");
        }
    }

    private ReplResult PollTargets()
    {
        if (_context is null)
        {
            return ReplResult.Success;
        }

        var result = ReplResult.Success;
        foreach (Core.Store.Session session in _context.Workspace.PollExitedAllocationProfiles())
        {
            Output.Success(console, $"Allocation profile captured for [bold]{session.Id}[/] [{Palette.Muted}]({session.Command})[/]");
        }

        foreach (TriggeredCaptureResult capture in _context.Workspace.PollTriggeredSnapshots())
        {
            if (!Output.TriggeredCapture(console, capture))
            {
                result |= ReplResult.Failure;
            }
        }
        return result;
    }

    private void PrintBanner(Workspace workspace)
    {
        if (workspace.Current is not null)
        {
            console.MarkupLineInterpolated($"[bold {Palette.Hot}]sl[/] [{Palette.Muted}]·[/] [{Palette.Name}]{workspace.CurrentName}[/] [{Palette.Muted}]loaded[/]");
        }
        else
        {
            int count = workspace.Store.Sessions.Count;
            string workspaces = count == 1 ? "workspace" : "workspaces";
            console.MarkupLineInterpolated($"[bold {Palette.Hot}]sl[/] [{Palette.Muted}]·[/] {count} {workspaces} [{Palette.Muted}]· no snapshot loaded[/]");
        }
        console.MarkupLine($"[{Palette.Muted}]type `help` for commands · `exit` to quit[/]");
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
