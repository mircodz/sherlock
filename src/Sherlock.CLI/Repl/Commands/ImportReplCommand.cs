using System.IO;
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
        if (args.Length == 0)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage}");
            return;
        }

        string path = args[0];
        if (!File.Exists(path))
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] file not found: {path}");
            return;
        }

        string? label = args.Length > 1 ? string.Join(' ', args[1..]) : null;

        SnapshotEntry entry = context.Workspace.Store.Register(
            sourcePath: path,
            moveIntoStore: false,
            origin: SnapshotOrigin.Import,
            label: label);

        context.Workspace.Load(entry);
        context.Console.MarkupLineInterpolated($"[green]imported & loaded[/] {entry.Id} [grey]({Path.GetFileName(entry.Path)})[/]");
    }
}
