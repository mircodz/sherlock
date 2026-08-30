using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Sherlock.Core.HeapModel;

/// <summary>
/// The heap graph's outbound-edge column, stored as one or more <c>int</c> chunks so it can exceed the
/// ~2.1&nbsp;billion-element ceiling of a single CLR array / <see cref="ReadOnlyMemory{T}"/>. A 30&nbsp;GB
/// dump can carry 1-3&nbsp;billion references; splitting them across node-aligned chunks (each cut only
/// at an object boundary) keeps any single node's successor run inside one chunk, so <see cref="Slice"/>
/// still returns a contiguous <see cref="ReadOnlySpan{Int32}"/>.
///
/// Positions are global <c>long</c> edge indices; <see cref="_chunkStartEdge"/> maps a chunk to its
/// first global index (ascending, with a terminating total), so locating a position's chunk is a tiny
/// binary search. An in-memory graph has a single chunk and the search degenerates to a bounds check.
/// </summary>
public sealed class EdgeColumn
{
    private readonly ReadOnlyMemory<int>[] _chunks;
    private readonly long[] _chunkStartEdge; // length chunks+1; [^1] == total edge count

    /// <summary>Total number of edges across every chunk.</summary>
    public long Count => _chunkStartEdge[^1];

    /// <summary>Number of physical chunks (1 for an in-memory graph).</summary>
    public int ChunkCount => _chunks.Length;

    /// <summary>Materializes the whole column as one array. Only for small columns (tests, tooling): a
    /// real graph's edges may exceed <see cref="int.MaxValue"/> and must be consumed via <see cref="Slice"/>.</summary>
    public int[] ToArray()
    {
        var all = new int[checked((int)Count)];
        int p = 0;
        foreach (ReadOnlyMemory<int> c in _chunks) { c.Span.CopyTo(all.AsSpan(p)); p += c.Length; }
        return all;
    }

    /// <summary>The <paramref name="i"/>-th chunk's edges (a view, not a copy).</summary>
    public ReadOnlyMemory<int> Chunk(int i) => _chunks[i];

    /// <summary>The chunk-start table (first global edge index of each chunk, length <see cref="ChunkCount"/>
    /// + 1 with the total last), persisted so a reload rebuilds the column without re-deriving it.</summary>
    public long[] ChunkStarts => _chunkStartEdge;

    /// <summary>Each chunk reinterpreted as raw little-endian bytes, for the container writer (which
    /// streams them back-to-back as separate sections).</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> ChunksAsBytes()
    {
        var result = new ReadOnlyMemory<byte>[_chunks.Length];
        for (int i = 0; i < _chunks.Length; i++)
        {
            result[i] = _chunks[i].Length == 0
                ? ReadOnlyMemory<byte>.Empty
                : new IntByteMemory(_chunks[i]).Memory;
        }
        return result;
    }

    /// <summary>A single-chunk column over an in-memory edge array (fresh extract / tests / small slab).</summary>
    public EdgeColumn(ReadOnlyMemory<int> edges)
    {
        _chunks = [edges];
        _chunkStartEdge = [0, edges.Length];
    }

    /// <summary>A multi-chunk column: <paramref name="chunks"/> concatenated logically, with
    /// <paramref name="chunkStartEdge"/> giving each chunk's first global edge index (length chunks+1,
    /// last entry the total). Every chunk boundary must fall on a node boundary so no successor run is
    /// split; the writer guarantees this.</summary>
    public EdgeColumn(ReadOnlyMemory<int>[] chunks, long[] chunkStartEdge)
    {
        if (chunkStartEdge.Length != chunks.Length + 1)
        {
            throw new ArgumentException($"chunkStartEdge length {chunkStartEdge.Length} must be chunks {chunks.Length} + 1.");
        }
        _chunks = chunks;
        _chunkStartEdge = chunkStartEdge;
    }

    /// <summary>The <paramref name="length"/> edges starting at global index <paramref name="start"/>,
    /// as a contiguous span. The run must lie within one chunk (guaranteed for a node's successors by
    /// node-aligned chunking).</summary>
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

    /// <summary>Assembles a node-aligned chunked column from ordered edge segments (the extractor's
    /// per-worker blocks, then the synthetic root's edges). <paramref name="offsets"/> is the CSR row
    /// table (length nodeCount + 1 == objectCount + 2); chunk boundaries fall only at node boundaries so
    /// no successor run is split, and a new chunk starts whenever adding the next node's edges would push
    /// the current chunk past <paramref name="maxEdgesPerChunk"/>. A single node's degree may itself
    /// exceed the budget, then that node gets its own (over-budget-but-under-2.1B) chunk, which the
    /// caller's budget must stay comfortably below <see cref="int.MaxValue"/> to allow.</summary>
    public static EdgeColumn Build(IReadOnlyList<ReadOnlyMemory<int>> segments, ReadOnlyMemory<long> offsets, long maxEdgesPerChunk)
    {
        long total = offsets.Span[^1];
        if (total <= maxEdgesPerChunk)
        {
            // Fits one chunk: concatenate (total < 2.1B by the budget). The common path.
            var single = new int[total];
            int p = 0;
            foreach (ReadOnlyMemory<int> seg in segments) { seg.Span.CopyTo(single.AsSpan(p)); p += seg.Length; }
            return new EdgeColumn(single);
        }

        // A cursor that reads the logical concatenation of the segments without joining them.
        var cursor = new SegmentCursor(segments);
        ReadOnlySpan<long> off = offsets.Span;
        int nodeCount = off.Length - 1;

        var chunks = new List<ReadOnlyMemory<int>>();
        var starts = new List<long> { 0 };
        long chunkStart = 0; // global edge index where the current chunk begins
        for (int node = 0; node < nodeCount; node++)
        {
            long nextBoundary = off[node + 1];
            // Close the current chunk before this node if adding it would overflow the budget (and the
            // chunk is non-empty, so we never emit a zero-length chunk).
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

    // Reads a fixed count of ints from a logical concatenation of segments, materializing each requested
    // run as one array (each run is one chunk, sized under the caller's budget < int.MaxValue).
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

    // The chunk index owning global edge position `pos` (binary search over the ascending starts).
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

    /// <summary>Feeds ~<paramref name="approxPoints"/> strided edge values to <paramref name="mix"/>,
    /// spread across all chunks, a cheap structural sample for the graph's content hash (a full pass over
    /// billions of edges would defeat the purpose).</summary>
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

/// <summary>Reinterprets a <c>ReadOnlyMemory&lt;int&gt;</c> as raw little-endian bytes, zero-copy: the
/// inverse of <see cref="Storage.Section.AsMemory{T}"/>, used to hand an edge chunk to the container
/// writer without copying. The source ints own the storage; this only re-types the view.</summary>
internal sealed unsafe class IntByteMemory(ReadOnlyMemory<int> ints) : MemoryManager<byte>
{
    private readonly ReadOnlyMemory<int> _ints = ints;

    public override Span<byte> GetSpan() =>
        MemoryMarshal.AsBytes(MemoryMarshal.AsMemory(_ints).Span);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        MemoryHandle h = _ints.Pin();
        return new MemoryHandle((byte*)h.Pointer + elementIndex, default, this);
    }

    public override void Unpin() { }
    protected override void Dispose(bool disposing) { }
}
