using System.IO;
using System.Threading;
using Sherlock.CLI.Rendering;
using Sherlock.CLI.Repl;
using Sherlock.Core;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI;

/// <summary>Shared entry points for building a workspace and driving the REPL.</summary>
internal static class ReplHost
{
    /// <summary>Creates a workspace over the default snapshot store.</summary>
    public static Workspace CreateWorkspace() => new(SnapshotStore.Default());

    /// <summary>Opens a dump as a transient current snapshot and runs the interactive REPL.</summary>
    public static int OpenAndRun(IAnsiConsole console, string dumpPath, CancellationToken cancellation = default)
    {
        using Workspace workspace = CreateWorkspace();
        try
        {
            workspace.LoadTransient(dumpPath);
        }
        catch (FileNotFoundException ex)
        {
            Output.Error(console, $"Dump file not found: {ex.FileName}");
            return 1;
        }
        catch (DumpAnalysisException ex)
        {
            Output.Error(console, $"{ex.Message}");
            return 1;
        }

        ReplResult result = RunInteractive(console, workspace, cancellation);
        return (result & (ReplResult.Failure | ReplResult.Cancelled)) != 0 ? 1 : 0;
    }

    /// <summary>Runs the interactive REPL against a workspace.</summary>
    public static ReplResult RunInteractive(IAnsiConsole console, Workspace workspace, CancellationToken cancellation = default)
    {
        var history = new ReplHistory(ReplHistory.DefaultPath);
        var repl = new Repl.Repl(ReplCommandRegistry.CreateDefault(history), history, console);
        return repl.RunInteractive(workspace, cancellation);
    }
}
