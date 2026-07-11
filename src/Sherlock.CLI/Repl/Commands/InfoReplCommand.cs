using System.IO;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Prints a high-level summary of the dump and its runtime.</summary>
public sealed class InfoReplCommand : IReplCommand
{
    public string Name => "info";
    public string Summary => "Show a summary of the dump, runtime and heap.";
    public string Usage => "info";

    public void Execute(ReplContext context, string[] args)
    {
        DumpInfo info = context.Snapshot.Info;

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Dump[/]", Markup.Escape(Path.GetFileName(info.DumpPath)));
        grid.AddRow("[grey]File size[/]", $"[bold green]{ByteSize.Format(info.FileSizeBytes)}[/]");
        grid.AddRow("[grey]Runtime[/]", Markup.Escape($"{info.ClrFlavor} {info.ClrVersion}"));
        grid.AddRow("[grey]Architecture[/]", Markup.Escape(info.Architecture));
        grid.AddRow("[grey]Platform[/]", Markup.Escape(info.Platform));
        grid.AddRow("[grey]Process id[/]", info.ProcessId?.ToString() ?? "[grey50]n/a[/]");
        grid.AddRow("[grey]GC mode[/]", info.ServerGc ? "Server" : "Workstation");
        grid.AddRow("[grey]Heaps[/]", info.HeapCount.ToString());
        grid.AddRow("[grey]Managed heap[/]", $"[bold green]{ByteSize.Format((long)info.TotalHeapBytes)}[/]");
        grid.AddRow("[grey]Threads[/]", info.ThreadCount.ToString());
        grid.AddRow("[grey]Modules[/]", info.ModuleCount.ToString());

        context.Console.MarkupLine("[bold]dump info[/]");
        context.Console.Write(grid);
    }
}
