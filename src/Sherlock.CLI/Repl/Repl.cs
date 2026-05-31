using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl;

/// <summary>
/// The interactive read-eval-print loop. Holds one open <see cref="DumpSession"/>
/// for the lifetime of the session and dispatches typed lines to commands.
/// </summary>
public sealed class Repl
{
    private const string Prompt = "sherlock> ";
    private static readonly string[] ExitWords = { "exit", "quit", "q" };

    private readonly ReplCommandRegistry _registry;
    private readonly ReplHistory _history;
    private readonly IAnsiConsole _console;

    private ReplContext? _context;
    private string? _lastCommand;

    public Repl(ReplCommandRegistry registry, ReplHistory history, IAnsiConsole console)
    {
        _registry = registry;
        _history = history;
        _console = console;
    }

    /// <summary>Runs commands non-interactively, then returns. Used by <c>--exec</c> and scripts.</summary>
    public void RunBatch(DumpSession session, IEnumerable<string> lines)
    {
        _context = new ReplContext(session, _console, RunLine);
        foreach (string line in lines)
        {
            _console.MarkupLineInterpolated($"[green]sherlock>[/] {line}");
            if (!RunLine(line))
                return;
        }
    }

    /// <summary>Runs the interactive loop until the user exits or input ends.</summary>
    public void RunInteractive(DumpSession session)
    {
        _context = new ReplContext(session, _console, RunLine);
        PrintBanner(session);

        while (true)
        {
            string? line = LineEditor.ReadLine(Prompt, _history);
            if (line is null) // EOF (Ctrl-D)
            {
                _console.WriteLine();
                return;
            }

            line = line.Trim();

            // Empty Enter repeats the previous command (gdb-style).
            if (line.Length == 0)
            {
                if (_lastCommand is null)
                    continue;
                line = _lastCommand;
                _console.MarkupLineInterpolated($"[grey]{Prompt}{line}[/]");
            }
            else
            {
                _history.Add(line);
                _lastCommand = line;
            }

            if (!RunLine(line))
                return;
        }
    }

    /// <summary>Dispatches one input line. Returns false when the loop should stop.</summary>
    private bool RunLine(string line)
    {
        string[] tokens = Tokenize(line);
        if (tokens.Length == 0)
            return true;

        string name = tokens[0];
        string[] args = tokens[1..];

        if (ExitWords.Contains(name, StringComparer.OrdinalIgnoreCase))
            return false;

        IReplCommand? command = _registry.Resolve(name);
        if (command is null)
        {
            _console.MarkupLineInterpolated($"[red]unknown command:[/] {name}. Type [bold]help[/] for a list.");
            return true;
        }

        try
        {
            command.Execute(_context!, args);
        }
        catch (DumpAnalysisException ex)
        {
            _console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
        }
        catch (Exception ex)
        {
            // Keep the session alive: one bad command shouldn't end the REPL.
            _console.MarkupLineInterpolated($"[red]{command.Name} failed:[/] {ex.Message}");
        }

        return true;
    }

    private void PrintBanner(DumpSession session)
    {
        _console.MarkupLineInterpolated($"[bold]Sherlock[/] — loaded [aqua]{Path.GetFileName(session.DumpPath)}[/]");
        _console.MarkupLine("Type [bold]help[/] for commands, [bold]exit[/] to quit.");
        _console.WriteLine();
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
            tokens.Add(current.ToString());

        return tokens.ToArray();
    }
}
