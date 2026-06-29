using System.Collections.Generic;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Removes a snapshot from the library (deleting the file if Sherlock owns it).</summary>
public sealed class RmReplCommand : IReplCommand
{
    public string Name => "rm";
    public IReadOnlyList<string> Aliases => ["delete"];
    public string Summary => "Remove a snapshot (sN) or a whole session (rN) from the library.";
    public string Usage => "rm <id>";
    public string Category => "Library";

    public void Execute(ReplContext context, string[] args)
    {
        if (args.Length == 0)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage}");
            return;
        }

        string id = args[0];

        // If we're deleting the loaded snapshot (or the session that owns it), unload first.
        if (context.Workspace.CurrentEntry?.Id == id || context.Workspace.CurrentSession?.Id == id)
        {
            context.Workspace.Unload();
        }

        if (!context.Workspace.Store.Remove(id))
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] no snapshot or session '{id}'.");
            return;
        }

        context.Console.MarkupLineInterpolated($"[grey]removed[/] {id}");
    }
}
