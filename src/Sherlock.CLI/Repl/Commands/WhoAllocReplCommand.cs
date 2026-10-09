using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;
using Sherlock.CLI.Rendering;
using Sherlock.Core.Profiling;
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

    public ReplResult Execute(ReplContext context, string[] args)
    {
        ulong address = Args.Address(args, 0, Usage);

        if (!context.Snapshot.HasCorrelation)
        {
            context.Console.MarkupLine(
                "[#FFAF00]This snapshot has no allocation provenance.[/] Capture one with " +
                "[bold]run --correlate -- <app>[/] then [bold]snapshot[/].");
            return ReplResult.Failure;
        }

        ClrObject obj = context.Snapshot.Runtime.Heap.GetObject(address);
        string typeLine = obj.Type is { } t
            ? $"[bold]{Markup.Escape(t.Name ?? "<unknown>")}[/] [#808791]({ByteSize.Format((long)obj.Size)})[/]"
            : "[#808791]<not a live object in this dump>[/]";
        context.Console.MarkupLine($"[#FFD75F]0x{address:x}[/]  {typeLine}");

        IReadOnlyList<string>? frames = context.Snapshot.WhoAllocated(address);
        if (frames is null)
        {
            context.Console.MarkupLine(
                "[#FFAF00]No allocation record.[/] [#808791]Untracked — allocated before profiling started, " +
                "sampled out, or freed & the slot reused since capture.[/]");
            return ReplResult.Success;
        }

        if (frames.Count == 0)
        {
            context.Console.MarkupLineInterpolated($"[#808791]{ProvenanceReader.NoManagedFrames}[/]");
            return ReplResult.Success;
        }

        // Stored root-first; display the allocation site first.
        context.Console.MarkupLine("[#808791]allocated at:[/]");
        for (int i = 0; i < frames.Count; i++)
        {
            context.Console.MarkupLineInterpolated($"  [#00D7FF]#{i}[/] {frames[frames.Count - 1 - i]}");
        }
        return ReplResult.Success;
    }
}
