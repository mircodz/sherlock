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
        if (args.Length == 0)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage}");
            return;
        }

        string? label = args.Length > 1 ? string.Join(' ', args[1..]) : null;
        SnapshotEntry? updated = context.Workspace.Store.SetLabel(args[0], label);

        if (updated is null)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] no snapshot '{args[0]}'.");
            return;
        }

        if (label is null)
        {
            context.Console.MarkupLineInterpolated($"[grey]cleared label on[/] {updated.Id}");
        }
        else
        {
            context.Console.MarkupLineInterpolated($"[green]labeled[/] {updated.Id} [grey]→[/] {label}");
        }
    }
}
