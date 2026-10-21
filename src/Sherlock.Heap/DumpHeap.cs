using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Diagnostics.Runtime;
using Sherlock.Storage;

namespace Sherlock.Heap;

/// <summary>A segment's object range, and the allocation contexts inside it, which the walk steps over.</summary>
internal sealed record HeapSegment(ulong Start, ulong End, bool SkipContexts, ulong[] ContextStarts, ulong[] ContextEnds)
{
    public ulong Bytes => End - Start;
}

/// <summary>
/// A dump's managed heap, ready for parallel walks: segments and allocation contexts from ClrMD, method tables
/// resolved once each, and object bytes read in place from the mapped dump.
/// </summary>
internal sealed class DumpHeap : IDisposable
{
    private DumpHeap(ClrHeap heap, DumpMemory memory, HeapSegment[] segments, TypeTable types)
    {
        Heap = heap;
        Memory = memory;
        Segments = segments;
        Types = types;
    }

    public ClrHeap Heap { get; }
    public DumpMemory Memory { get; }

    /// <summary>Segments holding objects, in address order.</summary>
    public HeapSegment[] Segments { get; }

    public TypeTable Types { get; }

    public static DumpHeap Open(string dumpPath, ClrRuntime runtime)
    {
        ClrHeap heap = runtime.Heap;
        if (runtime.DataTarget.DataReader.PointerSize != 8)
        {
            throw new NotSupportedException("Sherlock reads the heaps of 64-bit processes only.");
        }
        if (!heap.CanWalkHeap)
        {
            throw new InvalidDataException("The dump was taken during a garbage collection, so its heap can't be walked.");
        }

        // As in ClrMD's walk, a later context with the same start replaces an earlier one.
        var contexts = new Dictionary<ulong, ulong>();
        foreach (MemoryRange context in heap.EnumerateAllocationContexts())
        {
            contexts[context.Start] = context.End;
        }
        ulong[] starts = [.. contexts.Keys.Order()];
        ulong[] ends = [.. starts.Select(start => contexts[start])];

        var segments = new List<HeapSegment>();
        foreach (ClrSegment segment in heap.Segments.Where(s => s.ObjectRange.Length > 0).OrderBy(s => s.ObjectRange.Start))
        {
            ulong start = segment.ObjectRange.Start, end = segment.ObjectRange.End;
            if (segments.Count > 0 && start < segments[^1].End)
            {
                throw new InvalidDataException($"Heap segments at 0x{segments[^1].Start:x} and 0x{start:x} overlap.");
            }
            int first = HeapWalk.LowerBound(starts, start), last = HeapWalk.LowerBound(starts, end);
            bool skipContexts = segment.Kind is not (GCSegmentKind.Large or GCSegmentKind.Frozen);
            segments.Add(new HeapSegment(start, end, skipContexts, starts[first..last], ends[first..last]));
        }
        return new DumpHeap(heap, DumpMemory.Open(dumpPath), [.. segments], new TypeTable(heap, runtime.DataTarget.DataReader));
    }

    public void Dispose() => Memory.Dispose();
}
