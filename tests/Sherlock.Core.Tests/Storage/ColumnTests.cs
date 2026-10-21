using System;
using System.IO;
using System.Runtime.InteropServices;
using Sherlock.Core.Tests.Common;
using Sherlock.Storage;
using Xunit;

namespace Sherlock.Core.Tests.Storage;

public class ColumnTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private SlabFile WriteSlab<T>(SectionType type, T[] records) where T : unmanaged
    {
        var w = new ContainerWriter(SlabKind.Graph, 1);
        w.AddMemory<T>(type, records);
        return _tmp.WriteSlab(w, SlabKind.Graph, 1);
    }

    [Fact]
    public void IndexesEveryRecord()
    {
        var recs = new ulong[] { 10, 20, 30, 40, 50 };
        using var slab = WriteSlab(SectionType.GraphAddresses, recs);
        Column<ulong> col = slab.GetColumn<ulong>(SectionType.GraphAddresses);
        Assert.Equal(5L, col.Length);
        for (long i = 0; i < col.Length; i++) Assert.Equal(recs[i], col[i]);
    }

    [Fact]
    public void BinarySearchesByLongIndex()
    {
        // Match whoalloc's long-indexed search over the sorted object addresses.
        var addresses = new ulong[30];
        for (int i = 0; i < addresses.Length; i++) addresses[i] = (ulong)((i + 1) * 0x1000);
        using var slab = WriteSlab(SectionType.ObjectAddresses, addresses);
        Column<ulong> col = slab.GetColumn<ulong>(SectionType.ObjectAddresses);

        long Search(ulong address)
        {
            long lo = 0, hi = col.Length - 1;
            while (lo <= hi)
            {
                long mid = lo + ((hi - lo) >> 1);
                if (col[mid] == address)
                {
                    return mid;
                }
                if (col[mid] < address)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return -1;
        }

        for (int i = 1; i <= 30; i++)
            Assert.Equal(i - 1, Search((ulong)(i * 0x1000)));
        Assert.Equal(-1, Search(0x1500)); // untracked address between records
    }

    [Fact]
    public void SliceCopyToAndAsMemoryReadAnyRange()
    {
        var recs = new uint[24];
        for (uint i = 0; i < recs.Length; i++) recs[i] = i * 7;
        using var slab = WriteSlab(SectionType.GraphSizes, recs);
        Column<uint> col = slab.GetColumn<uint>(SectionType.GraphSizes);

        Assert.Equal(new uint[] { 14, 21, 28, 35 }, col.Slice(2, 4).ToArray());
        var dest = new uint[10];
        col.CopyTo(5, dest);
        Assert.Equal(recs[5..15], dest);
        Assert.Equal(recs[20..], col.AsMemory(20, 4).ToArray());
        Assert.Equal(recs, col.AsMemory().ToArray());
    }

    [Fact]
    public void MemoryComesFromTheMapping()
    {
        using var slab = WriteSlab(SectionType.GraphAddresses, new ulong[] { 1, 2, 3 });
        ReadOnlyMemory<ulong> memory = slab.GetColumn<ulong>(SectionType.GraphAddresses).AsMemory();
        Assert.True(MemoryMarshal.TryGetMemoryManager(memory, out PointerMemory<ulong>? _));
    }

    [Fact]
    public void EmptyAndAbsentColumns()
    {
        using var slab = WriteSlab(SectionType.GraphAddresses, Array.Empty<ulong>());
        Assert.Equal(0L, slab.GetColumn<ulong>(SectionType.GraphAddresses).Length);
        Assert.Equal(0L, slab.GetColumn<ulong>(SectionType.GraphSizes).Length);
        Assert.True(slab.Has(SectionType.GraphAddresses));
        Assert.False(slab.Has(SectionType.GraphSizes));
        Assert.True(slab.GetColumn<ulong>(SectionType.GraphSizes).AsMemory().IsEmpty);
    }

    [Fact]
    public void WrongElementWidthThrows()
    {
        using var slab = WriteSlab(SectionType.GraphAddresses, new ulong[] { 1, 2, 3 });
        Assert.Throws<InvalidDataException>(() => slab.GetColumn<uint>(SectionType.GraphAddresses));
    }

    [Fact]
    public void OutOfRangeReadsThrow()
    {
        using var slab = WriteSlab(SectionType.GraphAddresses, new ulong[] { 1, 2, 3 });
        Column<ulong> col = slab.GetColumn<ulong>(SectionType.GraphAddresses);
        Assert.Throws<ArgumentOutOfRangeException>(() => col[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => col[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => col.Slice(2, 2).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => col.AsMemory(-1, 1));
    }

    [Fact]
    public void SliceAtTheEndIsEmpty()
    {
        // An empty slice at the end must not dereference a one-past-end pointer.
        using var slab = WriteSlab(SectionType.GraphSizes, new uint[] { 1, 2, 3, 4 });
        Column<uint> col = slab.GetColumn<uint>(SectionType.GraphSizes);
        Assert.Equal(0, col.Slice(col.Length, 0).Length);
    }
}
