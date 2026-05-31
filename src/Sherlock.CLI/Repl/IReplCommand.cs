using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl;

/// <summary>The session and console a command operates against.</summary>
public sealed class ReplContext
{
    public ReplContext(DumpSession session, IAnsiConsole console, Func<string, bool> runLine)
    {
        Session = session;
        Console = console;
        RunLine = runLine;
    }

    public DumpSession Session { get; }
    public IAnsiConsole Console { get; }

    /// <summary>
    /// Dispatches a raw input line through the REPL (used by <c>source</c> to run
    /// script files). Returns false if the line requested exit.
    /// </summary>
    public Func<string, bool> RunLine { get; }
}

/// <summary>
/// A single analysis command. The same instances back both the interactive
/// REPL and non-interactive <c>--exec</c> invocations, so all analysis logic
/// lives in exactly one place.
/// </summary>
public interface IReplCommand
{
    /// <summary>Primary command name, e.g. <c>dumpheap</c>.</summary>
    string Name { get; }

    /// <summary>Alternate names, e.g. <c>dh</c>.</summary>
    IReadOnlyList<string> Aliases => Array.Empty<string>();

    /// <summary>One-line description shown by <c>help</c>.</summary>
    string Summary { get; }

    /// <summary>Usage string, e.g. <c>gcroot &lt;address&gt;</c>.</summary>
    string Usage { get; }

    /// <summary>Runs the command. <paramref name="args"/> excludes the command name itself.</summary>
    void Execute(ReplContext context, string[] args);
}
