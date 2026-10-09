using System.Linq;
using System.Globalization;
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
    public int MaxArgs => 0;

    public ReplResult Execute(ReplContext context, string[] args)
    {
        DumpInfo info = context.Snapshot.Info;

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow($"[{Palette.Muted}]File[/]", Markup.Escape(Path.GetFileName(info.DumpPath)));
        grid.AddRow($"[{Palette.Muted}]File size[/]", $"[bold {Palette.Text}]{ByteSize.Format(info.FileSizeBytes)}[/]");
        grid.AddRow($"[{Palette.Muted}]Runtime[/]", Markup.Escape($"{info.ClrFlavor} {info.ClrVersion}"));
        grid.AddRow($"[{Palette.Muted}]Architecture[/]", Markup.Escape(info.Architecture));
        grid.AddRow($"[{Palette.Muted}]Platform[/]", Markup.Escape(info.Platform));
        // Dumps from createdump omit the pid; the library records which process a snapshot came from.
        int? pid = info.ProcessId ?? context.Workspace.CurrentSession?.Processes
            .FirstOrDefault(process => process.Snapshots.Contains(context.Workspace.CurrentEntry!))?.Pid;
        grid.AddRow($"[{Palette.Muted}]Process id[/]", pid?.ToString(CultureInfo.InvariantCulture) ?? $"[{Palette.Muted}]n/a[/]");
        grid.AddRow($"[{Palette.Muted}]GC mode[/]", info.ServerGc ? "Server" : "Workstation");
        grid.AddRow($"[{Palette.Muted}]Heaps[/]", info.HeapCount.ToString(CultureInfo.InvariantCulture));
        grid.AddRow($"[{Palette.Muted}]Managed heap[/]", $"[bold {Palette.Text}]{ByteSize.Format((long)info.TotalHeapBytes)}[/]");
        grid.AddRow($"[{Palette.Muted}]Threads[/]", info.ThreadCount.ToString(CultureInfo.InvariantCulture));
        grid.AddRow($"[{Palette.Muted}]Modules[/]", info.ModuleCount.ToString(CultureInfo.InvariantCulture));

        string heading = context.Workspace.CurrentEntry is { } entry ? $"Snapshot {entry.Id}" : "Dump";
        context.Console.MarkupLine($"[bold {Palette.Heading}]{Markup.Escape(heading)}[/]");
        context.Console.Write(grid);
        return ReplResult.Success;
    }
}
