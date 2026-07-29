using System;
using Sherlock.Core.HeapModel;

namespace Sherlock.Core.Tests.HeapModel;

/// <summary>
/// Verifies <see cref="EdgeColumn"/> resolves global edge positions to the right chunk and returns
/// correct spans across chunk boundaries — the primitive that lets the edge column exceed the ~2.1B
/// single-array ceiling. Boundary math is off-by-one-prone, so these split a known sequence into
/// several uneven chunks and assert every sub-run reads back identically to the flat array.
/// </summary>
public sealed class EdgeColumnTests
{
    // 0,1,2,...,n-1 split into the given chunk sizes.
    private static (EdgeColumn Column, int[] Flat) Build(params int[] chunkSizes)
    {
        int total = 0;
        foreach (int s in chunkSizes) total += s;
        var flat = new int[total];
        for (int i = 0; i < total; i++) flat[i] = i;

        var chunks = new ReadOnlyMemory<int>[chunkSizes.Length];
        var starts = new long[chunkSizes.Length + 1];
        int pos = 0;
        for (int c = 0; c < chunkSizes.Length; c++)
        {
            chunks[c] = new ReadOnlyMemory<int>(flat, pos, chunkSizes[c]);
            starts[c] = pos;
            pos += chunkSizes[c];
        }
        starts[^1] = total;
        return (new EdgeColumn(chunks, starts), flat);
    }

    [Fact]
    public void SingleChunk_MatchesFlatArray()
    {
        var col = new EdgeColumn(new[] { 10, 20, 30, 40 });
        Assert.Equal(4, col.Count);
        Assert.Equal(1, col.ChunkCount);
        Assert.Equal([20, 30], col.Slice(1, 2).ToArray());
        Assert.Equal([10, 20, 30, 40], col.Slice(0, 4).ToArray());
    }

    [Fact]
    public void MultiChunk_SliceWithinEachChunk()
    {
        (EdgeColumn col, int[] flat) = Build(3, 5, 2, 7); // 17 edges over 4 chunks
        Assert.Equal(17, col.Count);
        Assert.Equal(4, col.ChunkCount);

        // A run that sits fully inside every chunk, addressed by its global start.
        foreach ((int start, int len) in new[] { (0, 3), (3, 5), (8, 2), (10, 7), (4, 1), (10, 1), (16, 1) })
        {
            Assert.Equal(flat.AsSpan(start, len).ToArray(), col.Slice(start, len).ToArray());
        }
    }

    [Fact]
    public void ChunkOf_ResolvesBoundariesExactly()
    {
        (EdgeColumn col, _) = Build(4, 4, 4); // starts at 0,4,8
        // First element of each chunk resolves to that chunk (value equals its global index).
        Assert.Equal([0], col.Slice(0, 1).ToArray());
        Assert.Equal([4], col.Slice(4, 1).ToArray());   // start of chunk 1
        Assert.Equal([8], col.Slice(8, 1).ToArray());   // start of chunk 2
        Assert.Equal([11], col.Slice(11, 1).ToArray()); // last element
    }

    [Fact]
    public void EmptySlice_ReturnsEmpty()
    {
        var col = new EdgeColumn(new[] { 1, 2, 3 });
        Assert.True(col.Slice(1, 0).IsEmpty);
    }

    [Fact]
    public void Sample_VisitsAcrossChunks()
    {
        (EdgeColumn col, _) = Build(100, 100, 100);
        var seen = new System.Collections.Generic.List<int>();
        col.Sample(30, v => seen.Add(v));
        Assert.NotEmpty(seen);
        // Strided sampling must reach into later chunks, not just the first.
        Assert.Contains(seen, v => v >= 200);
    }

    [Fact]
    public void RejectsMismatchedStartsLength()
    {
        var chunks = new[] { new ReadOnlyMemory<int>([1, 2]), new ReadOnlyMemory<int>([3]) };
        Assert.Throws<ArgumentException>(() => new EdgeColumn(chunks, [0, 2])); // needs length 3
    }
}
