using System.Collections.Generic;
using System.IO;
using Microsoft.Diagnostics.Runtime;
using Sherlock.CLI.Rendering;
using Sherlock.Core.Profiling;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// "Who allocated this?" — given an object address in the loaded snapshot, shows the
/// call stack that allocated it, joined from the snapshot's correlation sidecar (captured
/// alongside the dump for a <c>run --correlate</c> target).
/// </summary>
public sealed class WhoAllocReplCommand : IReplCommand
{
    public string Name => "whoalloc";
    public IReadOnlyList<string> Aliases => ["wa"];
    public string Summary => "Show where an object (address) was allocated from.";
    public string Category => "Allocation profiling";
    public string Usage => "whoalloc <address>";

    public void Execute(ReplContext context, string[] args)
    {
        if (args.Length == 0 || !Addresses.TryParse(args[0], out ulong address))
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage} (hex address, e.g. 0x141d2d0c0)");
            return;
        }

        // Provenance is a property of the loaded snapshot — this only works on snapshots.
        SnapshotEntry? entry = context.Workspace.CurrentEntry;
        string? sidecar = entry is null ? null : entry.Path + ".corr.tsv";
        if (sidecar is null || !File.Exists(sidecar))
        {
            context.Console.MarkupLine(
                "[yellow]This snapshot has no allocation provenance.[/] Capture one with " +
                "[bold]run --correlate -- <app>[/] then [bold]snapshot[/].");
            return;
        }

        // Object context from the heap (type + size), if the address resolves.
        ClrObject obj = context.Session.Runtime.Heap.GetObject(address);
        string typeLine = obj.Type is { } t
            ? $"[bold]{Markup.Escape(t.Name ?? "<unknown>")}[/] [grey]({ByteSize.Format((long)obj.Size)})[/]"
            : "[grey]<not a live object in this dump>[/]";
        context.Console.MarkupLine($"[grey]0x{address:x}[/]  {typeLine}");

        CorrelationMap map = CorrelationMap.Read(sidecar);
        string? folded = map.StackFor(address);
        if (folded is null)
        {
            context.Console.MarkupLine(
                "[yellow]No allocation record.[/] [grey]Untracked — allocated before profiling started, " +
                "sampled out, or freed & the slot reused since capture.[/]");
            return;
        }

        // Sidecar stack is folded root→leaf; show it backtrace-style, allocation site first.
        string[] frames = folded.Split(';');
        context.Console.MarkupLine("[grey]allocated at:[/]");
        for (int i = 0; i < frames.Length; i++)
        {
            string frame = frames[frames.Length - 1 - i]; // reverse to leaf→root
            context.Console.MarkupLineInterpolated($"  [aqua]#{i}[/] {frame}");
        }
    }
}
