using Sherlock.CLI.Rendering;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Sets or clears a snapshot's label.</summary>
public sealed class LabelReplCommand : IReplCommand
{
    public string Name => "label";
    public string Summary => "Label a snapshot (omit the name to clear it).";
    public string Usage => "label <id> [name]";
    public string Category => "Library";

    public void Execute(ReplContext context, string[] args)
    {
        Args.Require(args, 1, Usage);
        string? label = args.Length > 1 ? string.Join(' ', args[1..]) : null;
        SnapshotEntry? updated = context.Workspace.Store.SetLabel(args[0], label);

        if (updated is null)
        {
            Output.Error(context.Console, $"No snapshot '{args[0]}'.");
            return;
        }

        if (label is null)
        {
            Output.Success(context.Console, $"Cleared label on [bold]{updated.Id}[/]");
        }
        else
        {
            Output.Success(context.Console, $"Labeled [bold]{updated.Id}[/] · {label}");
        }
    }
}
