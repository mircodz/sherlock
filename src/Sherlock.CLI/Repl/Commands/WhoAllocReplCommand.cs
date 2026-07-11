using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;
using Sherlock.CLI.Rendering;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Shows the allocation call stack for an object address, from the snapshot's provenance.</summary>
public sealed class WhoAllocReplCommand : IReplCommand
{
    public string Name => "whoalloc";
    public IReadOnlyList<string> Aliases => ["wa"];
    public string Summary => "Show where an object (address) was allocated from.";
    public string Category => "Allocation profiling";
    public string Usage => "whoalloc <address>";

    public void Execute(ReplContext context, string[] args)
    {
        ulong address = Args.Address(args, 0, Usage);

        if (!context.Snapshot.HasCorrelation)
        {
            context.Console.MarkupLine(
                "[yellow]This snapshot has no allocation provenance.[/] Capture one with " +
                "[bold]run --correlate -- <app>[/] then [bold]snapshot[/].");
            return;
        }

        // Object context from the heap (type + size), if the address resolves.
        ClrObject obj = context.Snapshot.Runtime.Heap.GetObject(address);
        string typeLine = obj.Type is { } t
            ? $"[bold]{Markup.Escape(t.Name ?? "<unknown>")}[/] [grey]({ByteSize.Format((long)obj.Size)})[/]"
            : "[grey]<not a live object in this dump>[/]";
        context.Console.MarkupLine($"[grey]0x{address:x}[/]  {typeLine}");

        string? folded = context.Snapshot.WhoAllocated(address);
        if (folded is null)
        {
            context.Console.MarkupLine(
                "[yellow]No allocation record.[/] [grey]Untracked — allocated before profiling started, " +
                "sampled out, or freed & the slot reused since capture.[/]");
            return;
        }

        // Sidecar stack is folded root->leaf; show it backtrace-style, allocation site first.
        string[] frames = folded.Split(';');
        context.Console.MarkupLine("[grey]allocated at:[/]");
        for (int i = 0; i < frames.Length; i++)
        {
            string frame = frames[frames.Length - 1 - i]; // reverse to leaf→root
            context.Console.MarkupLineInterpolated($"  [aqua]#{i}[/] {frame}");
        }
    }
}
