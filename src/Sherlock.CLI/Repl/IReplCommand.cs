using System;
using System.Collections.Generic;
using Sherlock.Core;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl;

/// <summary>The workspace and console a command operates against.</summary>
public sealed record ReplContext(Workspace Workspace, IAnsiConsole Console, Func<string, bool> RunLine)
{
    /// <summary>The loaded snapshot, or a friendly error if nothing is loaded.</summary>
    public Snapshot Snapshot => Workspace.Current
        ?? throw new DumpAnalysisException("No snapshot loaded. Use `load <id>`, `collect`, or `import <file>` first.");

    /// <summary>Resolves a snapshot by id or label, erroring if it's unknown or its file is gone.</summary>
    public SnapshotEntry ResolveSnapshot(string idOrLabel)
    {
        if (Workspace.Store.FindSnapshot(idOrLabel) is not (_, { } snap))
        {
            throw new DumpAnalysisException($"no snapshot '{idOrLabel}'. See `snapshots`.");
        }
        if (!snap.Exists)
        {
            throw new DumpAnalysisException($"snapshot '{idOrLabel}' file is missing.");
        }
        return snap;
    }
}

/// <summary>
/// An analysis command. The same instances back both the interactive REPL and <c>--exec</c>,
/// so analysis logic lives in one place.
/// </summary>
public interface IReplCommand
{
    /// <summary>Primary command name, e.g. <c>dumpheap</c>.</summary>
    string Name { get; }

    /// <summary>Alternate names, e.g. <c>dh</c>.</summary>
    IReadOnlyList<string> Aliases => [];

    /// <summary>One-line description shown by <c>help</c>.</summary>
    string Summary { get; }

    /// <summary>Group heading under which <c>help</c> lists this command.</summary>
    string Category => "Analysis";

    /// <summary>Usage string, e.g. <c>gcroot &lt;address&gt;</c>.</summary>
    string Usage { get; }

    /// <summary>Runs the command. <paramref name="args"/> excludes the command name itself.</summary>
    void Execute(ReplContext context, string[] args);
}
