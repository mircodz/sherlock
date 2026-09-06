using System.Threading;
using Sherlock.CLI.Tui;
using Spectre.Console.Cli;

namespace Sherlock.CLI.Commands;

/// <summary>Opens the TUI over the snapshot library.</summary>
public sealed class TuiCommand : Command<TuiCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
        => SnapshotExplorer.Run().GetAwaiter().GetResult();
}
