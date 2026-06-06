using System.IO;
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

    /// <summary>
    /// Opens a dump as a transient current snapshot and runs the interactive REPL.
    /// Used by <c>collect --analyze</c> and <c>run … snapshot --analyze</c>.
    /// </summary>
    public static int OpenAndRun(IAnsiConsole console, string dumpPath)
    {
        using Workspace workspace = CreateWorkspace();
        try
        {
            workspace.LoadTransient(dumpPath);
        }
        catch (FileNotFoundException ex)
        {
            console.MarkupLineInterpolated($"[red]error:[/] dump file not found: {ex.FileName}");
            return 1;
        }
        catch (DumpAnalysisException ex)
        {
            console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 1;
        }

        RunInteractive(console, workspace);
        return 0;
    }

    /// <summary>Runs the interactive REPL against a workspace.</summary>
    public static void RunInteractive(IAnsiConsole console, Workspace workspace)
    {
        var history = new ReplHistory(ReplHistory.DefaultPath);
        var repl = new Repl.Repl(ReplCommandRegistry.CreateDefault(history), history, console);
        repl.RunInteractive(workspace);
    }
}
