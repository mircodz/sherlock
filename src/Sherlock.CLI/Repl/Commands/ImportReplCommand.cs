using System.IO;
using Sherlock.CLI.Rendering;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Adds an existing dump file to the library (by reference) and loads it.</summary>
public sealed class ImportReplCommand : IReplCommand
{
    public string Name => "import";
    public string Summary => "Add a dump file to the library and load it.";
    public string Usage => "import <file> [label]";
    public string Category => "Library";

    public void Execute(ReplContext context, string[] args)
    {
        Args.Require(args, 1, Usage);
        string path = args[0];
        if (!File.Exists(path))
        {
            Output.Error(context.Console, $"File not found: {path}");
            return;
        }

        string? label = args.Length > 1 ? string.Join(' ', args[1..]) : null;

        (Session session, SnapshotEntry entry) = context.Workspace.Store.RegisterStandalone(
            SessionKind.Import,
            sourcePath: path,
            moveIntoStore: false,
            label: label);

        context.Workspace.Load(session, entry);
        Output.Success(context.Console, $"Imported and loaded [bold]{entry.Id}[/] [#808791]({Path.GetFileName(entry.Path)})[/]");
    }
}
