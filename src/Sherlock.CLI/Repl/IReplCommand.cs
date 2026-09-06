using System;
using System.Collections.Generic;
using System.Threading;
using Sherlock.Core;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl;

/// <summary>The workspace and console a command operates against.</summary>
public sealed record ReplContext(
    Workspace Workspace,
    IAnsiConsole Console,
    Func<string, ReplResult> RunLine,
    CancellationToken Cancellation = default)
{
    internal HashSet<string> ActiveScripts { get; } = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public Snapshot Snapshot => Workspace.Current
        ?? throw new DumpAnalysisException("No snapshot loaded. Use `load <id>`, `collect`, or `import <file>` first.");

    /// <summary>Resolves an existing snapshot by ID or label.</summary>
    public SnapshotEntry ResolveSnapshot(string idOrLabel)
    {
        if (Workspace.Store.FindSnapshot(idOrLabel) is not (_, { } snap))
        {
            throw new DumpAnalysisException($"no snapshot '{idOrLabel}'. See `ls`.");
        }
        if (!snap.Exists)
        {
            throw new DumpAnalysisException($"snapshot '{idOrLabel}' file is missing.");
        }
        return snap;
    }
}

/// <summary>Batch results retain failures even when a later command quits or is cancelled.</summary>
[Flags]
public enum ReplResult
{
    Success = 0,
    Failure = 1,
    Quit = 2,
    Cancelled = 4,
}

/// <summary>An analysis command, shared by both the interactive REPL and <c>--exec</c>.</summary>
public interface IReplCommand
{
    string Name { get; }

    IReadOnlyList<string> Aliases => [];

    /// <summary>Description shown by help.</summary>
    string Summary { get; }

    /// <summary>Group heading in help.</summary>
    string Category => "Analysis";

    string Usage { get; }

    /// <summary>Arguments exclude the command name.</summary>
    ReplResult Execute(ReplContext context, string[] args);
}
