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
        grid.AddRow("[#808791]Dump[/]", Markup.Escape(Path.GetFileName(info.DumpPath)));
        grid.AddRow("[#808791]File size[/]", $"[bold #F2F2F2]{ByteSize.Format(info.FileSizeBytes)}[/]");
        grid.AddRow("[#808791]Runtime[/]", Markup.Escape($"{info.ClrFlavor} {info.ClrVersion}"));
        grid.AddRow("[#808791]Architecture[/]", Markup.Escape(info.Architecture));
        grid.AddRow("[#808791]Platform[/]", Markup.Escape(info.Platform));
        grid.AddRow("[#808791]Process id[/]", info.ProcessId?.ToString() ?? "[#808791]n/a[/]");
        grid.AddRow("[#808791]GC mode[/]", info.ServerGc ? "Server" : "Workstation");
        grid.AddRow("[#808791]Heaps[/]", info.HeapCount.ToString());
        grid.AddRow("[#808791]Managed heap[/]", $"[bold #F2F2F2]{ByteSize.Format((long)info.TotalHeapBytes)}[/]");
        grid.AddRow("[#808791]Threads[/]", info.ThreadCount.ToString());
        grid.AddRow("[#808791]Modules[/]", info.ModuleCount.ToString());

        context.Console.MarkupLine("[bold]dump info[/]");
        context.Console.Write(grid);
    }
}
