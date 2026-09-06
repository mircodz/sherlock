using System.Threading;
using Sherlock.CLI.Repl;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI;

/// <summary>Default workspace and interactive REPL setup.</summary>
internal static class ReplHost
{
    public static Workspace CreateWorkspace() => new(SnapshotStore.Default());

    public static ReplResult RunInteractive(IAnsiConsole console, Workspace workspace, CancellationToken cancellation = default)
    {
        var history = new ReplHistory(ReplHistory.DefaultPath);
        var repl = new Repl.Repl(ReplCommandRegistry.CreateDefault(history), history, console);
        return repl.RunInteractive(workspace, cancellation);
    }
}
