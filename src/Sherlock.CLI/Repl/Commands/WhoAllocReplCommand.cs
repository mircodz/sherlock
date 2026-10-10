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
    public int MaxArgs => 1;

    public ReplResult Execute(ReplContext context, string[] args)
    {
        ulong address = Args.Address(args, 0, Usage);

        if (!context.Snapshot.HasCorrelation)
        {
            context.Console.MarkupLine(
                $"[{Palette.Warning}]This snapshot has no allocation provenance.[/] Capture one with " +
                "[bold]run --correlate -- <app>[/] then [bold]snapshot[/].");
            return ReplResult.Failure;
        }

        ClrObject obj = context.Snapshot.Runtime.Heap.GetObject(address);
        context.Console.MarkupLine(obj.Type is { } t
            ? Styled.Object(t.Name ?? "<unknown>", address, (long)obj.Size)
            : $"{Styled.Address(address)}  [{Palette.Muted}]<not a live object in this dump>[/]");

        IReadOnlyList<string>? frames = context.Snapshot.WhoAllocated(address);
        if (frames is null)
        {
            context.Console.MarkupLine(
                $"[{Palette.Warning}]No allocation record.[/] [{Palette.Muted}]Untracked — allocated before profiling started, " +
                "sampled out, or freed & the slot reused since capture.[/]");
            return ReplResult.Success;
        }

        if (frames.Count == 0)
        {
            context.Console.MarkupLineInterpolated($"[{Palette.Muted}]{ProvenanceReader.NoManagedFrames}[/]");
            return ReplResult.Success;
        }

        // Stored root-first; display the allocation site first.
        context.Console.MarkupLine($"[{Palette.Muted}]allocated at:[/]");
        var stack = new Grid().AddColumn(new GridColumn().Padding(2, 0, 2, 0).NoWrap()).AddColumn().AddColumn();
        for (int i = 0; i < frames.Count; i++)
        {
            string frame = frames[frames.Count - 1 - i];
            stack.AddRow(Styled.Muted($"#{i}"), Styled.Method(frame), Styled.Namespace(FrameNames.Split(frame).Type));
        }
        context.Console.Write(stack);
        return ReplResult.Success;
    }
}
