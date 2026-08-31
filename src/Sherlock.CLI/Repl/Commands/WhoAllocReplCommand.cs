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
                "[#FFAF00]This snapshot has no allocation provenance.[/] Capture one with " +
                "[bold]run --correlate -- <app>[/] then [bold]snapshot[/].");
            return;
        }

        // Heap type + size, if the address resolves to a live object.
        ClrObject obj = context.Snapshot.Runtime.Heap.GetObject(address);
        string typeLine = obj.Type is { } t
            ? $"[bold]{Markup.Escape(t.Name ?? "<unknown>")}[/] [#808791]({ByteSize.Format((long)obj.Size)})[/]"
            : "[#808791]<not a live object in this dump>[/]";
        context.Console.MarkupLine($"[#FFD75F]0x{address:x}[/]  {typeLine}");

        string? folded = context.Snapshot.WhoAllocated(address);
        if (folded is null)
        {
            context.Console.MarkupLine(
                "[#FFAF00]No allocation record.[/] [#808791]Untracked — allocated before profiling started, " +
                "sampled out, or freed & the slot reused since capture.[/]");
            return;
        }

        // Folded stack is root->leaf; show backtrace-style, allocation site first.
        string[] frames = folded.Split(';');
        context.Console.MarkupLine("[#808791]allocated at:[/]");
        for (int i = 0; i < frames.Length; i++)
        {
            string frame = frames[frames.Length - 1 - i]; // leaf->root
            context.Console.MarkupLineInterpolated($"  [#00D7FF]#{i}[/] {frame}");
        }
    }
}
