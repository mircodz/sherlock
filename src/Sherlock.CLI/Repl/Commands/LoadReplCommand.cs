using System.IO;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Loads a snapshot from the library as the current analysis target.</summary>
public sealed class LoadReplCommand : IReplCommand
{
    public string Name => "load";
    public string Summary => "Load a snapshot from the library by id or label.";
    public string Usage => "load <id>";
    public string Category => "Library";

    public void Execute(ReplContext context, string[] args)
    {
        if (args.Length == 0)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage}");
            return;
        }

        if (context.Workspace.Store.FindSnapshot(args[0]) is not ({ } session, { } entry))
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] no snapshot '{args[0]}'. Use [bold]snapshots[/] to list.");
            return;
        }

        if (!entry.Exists)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] dump file is missing: {entry.Path}");
            return;
        }

        context.Workspace.Load(session, entry);
        context.Console.MarkupLineInterpolated($"[green]loaded[/] {entry.Id} [grey]({Path.GetFileName(entry.Path)})[/]");
    }
}
