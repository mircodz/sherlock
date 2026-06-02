using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Removes a snapshot from the library (deleting the file if Sherlock owns it).</summary>
public sealed class RmReplCommand : IReplCommand
{
    public string Name => "rm";
    public IReadOnlyList<string> Aliases => new[] { "delete" };
    public string Summary => "Remove a snapshot from the library.";
    public string Usage => "rm <id>";
    public string Category => "Library";

    public void Execute(ReplContext context, string[] args)
    {
        if (args.Length == 0)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage}");
            return;
        }

        SnapshotEntry? entry = context.Workspace.Store.Get(args[0]);
        if (entry is null)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] no snapshot '{args[0]}'.");
            return;
        }

        // If we're deleting what's currently loaded, unload it first.
        if (context.Workspace.CurrentEntry?.Id == entry.Id)
            context.Workspace.Unload();

        context.Workspace.Store.Remove(entry.Id);
        context.Console.MarkupLineInterpolated(
            $"[grey]removed[/] {entry.Id}{(entry.Owned ? " and deleted its dump" : " (file left in place)")}");
    }
}
