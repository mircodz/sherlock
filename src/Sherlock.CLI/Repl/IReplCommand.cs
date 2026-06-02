using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl;

/// <summary>The workspace and console a command operates against.</summary>
public sealed class ReplContext
{
    public ReplContext(Workspace workspace, IAnsiConsole console, Func<string, bool> runLine)
    {
        Workspace = workspace;
        Console = console;
        RunLine = runLine;
    }

    public Workspace Workspace { get; }
    public IAnsiConsole Console { get; }

    /// <summary>
    /// The currently-loaded session. Analysis commands use this; if nothing is
    /// loaded it raises a friendly error the REPL renders.
    /// </summary>
    public DumpSession Session => Workspace.Current
        ?? throw new DumpAnalysisException("No snapshot loaded. Use `load <id>`, `collect`, or `import <file>` first.");

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

    /// <summary>Group heading under which <c>help</c> lists this command.</summary>
    string Category => "Analysis";

    /// <summary>Usage string, e.g. <c>gcroot &lt;address&gt;</c>.</summary>
    string Usage { get; }

    /// <summary>Runs the command. <paramref name="args"/> excludes the command name itself.</summary>
    void Execute(ReplContext context, string[] args);
}
