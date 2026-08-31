using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sherlock.Core.Storage;

namespace Sherlock.Core.HeapModel;

/// <summary>A long-indexed edge column split only at CSR row boundaries.</summary>
public sealed class EdgeColumn
{
    private readonly ReadOnlyMemory<int>[] _chunks;
    private readonly long[] _chunkStartEdge;

    /// <summary>Total number of edges across every chunk.</summary>
    public long Count => _chunkStartEdge[^1];

    /// <summary>Number of physical chunks (1 for an in-memory graph).</summary>
    public int ChunkCount => _chunks.Length;

    /// <summary>Materializes columns that fit one CLR array.</summary>
    public int[] ToArray()
    {
        var all = new int[checked((int)Count)];
        int p = 0;
        foreach (ReadOnlyMemory<int> c in _chunks) { c.Span.CopyTo(all.AsSpan(p)); p += c.Length; }
        return all;
    }

    public ReadOnlyMemory<int> Chunk(int i) => _chunks[i];

    /// <summary>Global start of each chunk, followed by the total edge count.</summary>
    public long[] ChunkStarts => _chunkStartEdge;

    public IReadOnlyList<ReadOnlyMemory<byte>> ChunksAsBytes()
    {
        var result = new ReadOnlyMemory<byte>[_chunks.Length];
        for (int i = 0; i < _chunks.Length; i++)
        {
            result[i] = _chunks[i].Length == 0
                ? ReadOnlyMemory<byte>.Empty
                : new UnmanagedByteMemory<int>(_chunks[i]).Memory;
        }
        return result;
    }

    public EdgeColumn(ReadOnlyMemory<int> edges)
    {
        _chunks = [edges];
        _chunkStartEdge = [0, edges.Length];
    }

    /// <summary>Creates a logical column from node-aligned chunks.</summary>
    public EdgeColumn(ReadOnlyMemory<int>[] chunks, long[] chunkStartEdge)
    {
        if (chunkStartEdge.Length != chunks.Length + 1)
        {
            throw new ArgumentException($"chunkStartEdge length {chunkStartEdge.Length} must be chunks {chunks.Length} + 1.");
        }
        if (chunkStartEdge.Length == 0 || chunkStartEdge[0] != 0)
        {
            throw new ArgumentException("the first edge chunk must start at zero.");
        }
        for (int i = 0; i < chunks.Length; i++)
        {
            if (chunkStartEdge[i + 1] < chunkStartEdge[i] || chunkStartEdge[i + 1] - chunkStartEdge[i] != chunks[i].Length)
            {
                throw new ArgumentException($"edge chunk {i} does not match its start table.");
            }
        }
        _chunks = chunks;
        _chunkStartEdge = chunkStartEdge;
    }

    /// <summary>Returns a range that lies within one chunk.</summary>
    public ReadOnlySpan<int> Slice(long start, int length)
    {
        if (length == 0)
        {
            return default;
        }
        int c = ChunkOf(start);
        long local = start - _chunkStartEdge[c];
        return _chunks[c].Span.Slice((int)local, length);
    }

    /// <summary>Builds chunks without splitting a node's successors.</summary>
    public static EdgeColumn Build(IReadOnlyList<ReadOnlyMemory<int>> segments, ReadOnlyMemory<long> offsets, long maxEdgesPerChunk)
    {
        long total = offsets.Span[^1];
        if (total <= maxEdgesPerChunk)
        {
            var single = new int[total];
            int p = 0;
            foreach (ReadOnlyMemory<int> seg in segments) { seg.Span.CopyTo(single.AsSpan(p)); p += seg.Length; }
            return new EdgeColumn(single);
        }

        var cursor = new SegmentCursor(segments);
        ReadOnlySpan<long> off = offsets.Span;
        int nodeCount = off.Length - 1;

        var chunks = new List<ReadOnlyMemory<int>>();
        var starts = new List<long> { 0 };
        long chunkStart = 0;
        for (int node = 0; node < nodeCount; node++)
        {
            long nextBoundary = off[node + 1];
            if (off[node] > chunkStart && nextBoundary - chunkStart > maxEdgesPerChunk)
            {
                chunks.Add(cursor.Take((int)(off[node] - chunkStart)));
                starts.Add(off[node]);
                chunkStart = off[node];
            }
        }
        chunks.Add(cursor.Take((int)(total - chunkStart)));
        starts.Add(total);
        return new EdgeColumn(chunks.ToArray(), starts.ToArray());
    }

    private sealed class SegmentCursor(IReadOnlyList<ReadOnlyMemory<int>> segments)
    {
        private int _seg;
        private int _within;

        public ReadOnlyMemory<int> Take(int count)
        {
            var buffer = new int[count];
            int filled = 0;
            while (filled < count)
            {
                ReadOnlySpan<int> src = segments[_seg].Span[_within..];
                int take = Math.Min(src.Length, count - filled);
                src[..take].CopyTo(buffer.AsSpan(filled));
                filled += take;
                _within += take;
                if (_within == segments[_seg].Length) { _seg++; _within = 0; }
            }
            return buffer;
        }
    }

    private int ChunkOf(long pos)
    {
        if (_chunks.Length == 1)
        {
            return 0;
        }
        int lo = 0, hi = _chunks.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_chunkStartEdge[mid] <= pos)
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

    /// <summary>Samples values across all chunks.</summary>
    public void Sample(int approxPoints, Action<int> mix)
    {
        long total = Count;
        if (total == 0)
        {
            return;
        }
        long step = Math.Max(1, total / Math.Max(1, approxPoints));
        for (long pos = 0; pos < total; pos += step)
        {
            int c = ChunkOf(pos);
            mix(_chunks[c].Span[(int)(pos - _chunkStartEdge[c])]);
        }
    }
}
