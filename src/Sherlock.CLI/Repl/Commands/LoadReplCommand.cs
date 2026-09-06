using System.IO;
using Sherlock.CLI.Rendering;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Loads a snapshot from the library as the current analysis target.</summary>
public sealed class LoadReplCommand : IReplCommand
{
    public string Name => "load";
    public string Summary => "Load a snapshot from the library by id or label.";
    public string Usage => "load <id>";
    public string Category => "Library";

    public ReplResult Execute(ReplContext context, string[] args)
    {
        Args.Require(args, 1, Usage);

        if (context.Workspace.Store.FindSnapshot(args[0]) is not ({ } session, { } entry))
        {
            Output.Error(context.Console, $"No snapshot '{args[0]}'. Use [bold]ls[/] to list.");
            return ReplResult.Failure;
        }

        if (!entry.Exists)
        {
            Output.Error(context.Console, $"Dump file is missing: {entry.Path}");
            return ReplResult.Failure;
        }

        context.Workspace.Load(session, entry);
        Output.Success(context.Console, $"Loaded [bold]{entry.Id}[/] [#808791]({Path.GetFileName(entry.Path)})[/]");
        return ReplResult.Success;
    }
}
