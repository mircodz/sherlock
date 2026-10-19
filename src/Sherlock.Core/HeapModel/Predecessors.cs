using System;
using System.Collections.Generic;
using System.Threading;

namespace Sherlock.Core.HeapModel;

/// <summary>Who references each object: the graph's reference edges reversed, sources in ascending order. Costs an
/// <c>int</c> per edge and a <c>long</c> per object.</summary>
internal sealed class Predecessors
{
    // Each array ends on an object boundary, so every object's referrers are one span.
    private const int MaxChunk = 1 << 30;

    private readonly long[] _offsets;
    private readonly int[][] _chunks;
    private readonly long[] _chunkStarts;

    private Predecessors(long[] offsets, int[][] chunks, long[] chunkStarts)
    {
        _offsets = offsets;
        _chunks = chunks;
        _chunkStarts = chunkStarts;
    }

    /// <summary>The objects that reference <paramref name="objectId"/>. GC roots aren't included.</summary>
    public ReadOnlySpan<int> Of(int objectId)
    {
        long start = _offsets[objectId];
        int length = (int)(_offsets[objectId + 1] - start);
        if (length == 0)
        {
            return default;
        }
        int chunk = ChunkOf(start);
        return _chunks[chunk].AsSpan((int)(start - _chunkStarts[chunk]), length);
    }

    public static Predecessors Build(HeapGraph graph, CancellationToken cancellationToken) => Build(graph, cancellationToken, MaxChunk);

    internal static Predecessors Build(HeapGraph graph, CancellationToken cancellationToken, int maxChunk)
    {
        int count = graph.ObjectCount;
        var offsets = new long[count + 1];
        for (int source = 0; source < count; source++)
        {
            if ((source & 0xffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            foreach (int target in graph.Successors(source))
            {
                offsets[target + 1]++;
            }
        }

        // Cut the sources into arrays between objects, before allocating any of them.
        var chunkStarts = new List<long> { 0 };
        long chunkStart = 0, position = 0;
        for (int target = 0; target < count; target++)
        {
            long referrers = offsets[target + 1];
            if (referrers > Array.MaxLength)
            {
                throw new NotSupportedException($"Object {target} is referenced {referrers:N0} times, more than Sherlock can trace.");
            }
            if (position + referrers - chunkStart > maxChunk && position > chunkStart)
            {
                chunkStarts.Add(chunkStart = position);
            }
            position += referrers;
            offsets[target + 1] = position;
        }
        chunkStarts.Add(position);

        var chunks = new int[chunkStarts.Count - 1][];
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i] = new int[chunkStarts[i + 1] - chunkStarts[i]];
        }
        var predecessors = new Predecessors(offsets, chunks, [.. chunkStarts]);

        // Fill using each object's start as its cursor; afterwards each start holds the next object's, so shift back.
        for (int source = 0; source < count; source++)
        {
            if ((source & 0xffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            foreach (int target in graph.Successors(source))
            {
                long slot = offsets[target]++;
                int chunk = predecessors.ChunkOf(slot);
                chunks[chunk][slot - predecessors._chunkStarts[chunk]] = source;
            }
        }
        Array.Copy(offsets, 0, offsets, 1, count);
        offsets[0] = 0;
        return predecessors;
    }

    private int ChunkOf(long position)
    {
        int lo = 0, hi = _chunks.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_chunkStarts[mid] <= position)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return lo;
    }
}
