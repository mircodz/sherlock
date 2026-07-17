using System.Threading;
using Sherlock.CLI.Tui;
using Spectre.Console.Cli;

namespace Sherlock.CLI.Commands;

/// <summary>
/// Opens the interactive heap explorer (a Tessera TUI) over the snapshot library: pick a snapshot,
/// then drill through the Health / Types / Retention / Allocations lenses with clickable, linked
/// navigation. Needs at least one captured snapshot (<c>sl run</c> / <c>sl collect</c>).
/// </summary>
public sealed class TuiCommand : Command<TuiCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
        => SnapshotExplorer.Run().GetAwaiter().GetResult();
}
