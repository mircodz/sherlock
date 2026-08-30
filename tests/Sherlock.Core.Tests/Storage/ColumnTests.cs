using System;
using System.IO;
using System.Runtime.InteropServices;
using Sherlock.Core.Storage;
using Sherlock.Core.Tests.Common;

namespace Sherlock.Core.Tests.Storage;

// Tests for the unified long-indexed column layer: SlabFile.Open + Column<T>. A column reads identically
// whether it's one on-disk section or many, with indexing / binary-search / slicing working across
// section and mmap-chunk boundaries and no ~2 GB single-section ceiling.
public class ColumnTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    // Writes `records` split into `sections` same-typed sections (mirrors the native addChunkedRecords
    // N-section layout) and opens the result as a SlabFile.
    private SlabFile WriteSlab<T>(SectionType type, ushort version, ReadOnlySpan<T> records, int sections)
        where T : unmanaged
    {
        var w = new ContainerWriter();
        int n = records.Length;
        if (sections <= 1 || n == 0)
        {
            w.AddRecords(type, version, records);
        }
        else
        {
            int per = (n + sections - 1) / sections;
            for (int i = 0; i < n; i += per)
            {
                w.AddRecords(type, version, records.Slice(i, Math.Min(per, n - i)));
            }
        }
        return _tmp.WriteSlab(w);
    }

    [Fact]
    public void SingleSectionColumnRoundTrips()
    {
        var recs = new ulong[] { 10, 20, 30, 40, 50 };
        using var slab = WriteSlab<ulong>(SectionType.GraphAddresses, 3, recs, sections: 1);
        Column<ulong> col = slab.GetColumn<ulong>(SectionType.GraphAddresses);
        Assert.Equal(5L, col.Length);
        for (long i = 0; i < col.Length; i++) Assert.Equal(recs[i], col[i]);
    }

    [Fact]
    public void MultiSectionColumnReassemblesInOrder()
    {
        var recs = new long[20];
        for (int i = 0; i < recs.Length; i++) recs[i] = i * 1000L;
        // 4 sections of 5 elements each.
        using var slab = WriteSlab<long>(SectionType.GraphOffsets, 3, recs, sections: 4);
        Column<long> col = slab.GetColumn<long>(SectionType.GraphOffsets);
        Assert.Equal(20L, col.Length);
        for (long i = 0; i < col.Length; i++)
            Assert.Equal(recs[i], col[i]); // global order preserved across the 4 sections
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Corr { public ulong Address; public uint StackId; public uint Reserved; }

    [Fact]
    public void BinarySearchWorksAcrossSectionBoundaries()
    {
        // Mirrors the whoalloc correlation lookup: a sorted column split into several sections, searched
        // by a long-indexed binary search that must resolve addresses in any section.
        var recs = new Corr[30];
        for (int i = 0; i < recs.Length; i++)
            recs[i] = new Corr { Address = (ulong)((i + 1) * 0x1000), StackId = (uint)(i % 3), Reserved = 0 };
        using var slab = WriteSlab<Corr>(SectionType.Correlation, 2, recs, sections: 6);
        Column<Corr> col = slab.GetColumn<Corr>(SectionType.Correlation);
        Assert.Equal(30L, col.Length);

        uint Search(ulong addr)
        {
            long lo = 0, hi = col.Length - 1;
            while (lo <= hi)
            {
                long mid = lo + ((hi - lo) >> 1);
                ulong a = col[mid].Address;
                if (a == addr) return col[mid].StackId;
                if (a < addr) lo = mid + 1; else hi = mid - 1;
            }
            return uint.MaxValue;
        }

        for (int i = 1; i <= 30; i++)
            Assert.Equal((uint)((i - 1) % 3), Search((ulong)(i * 0x1000)));
        Assert.Equal(uint.MaxValue, Search(0x1500)); // untracked address between records
    }

    [Fact]
    public void SliceIsZeroCopyWithinASectionAndCopyToStraddles()
    {
        var recs = new uint[24];
        for (uint i = 0; i < recs.Length; i++) recs[i] = i * 7;
        using var slab = WriteSlab<uint>(SectionType.GraphSizes, 3, recs, sections: 3); // 8/section
        Column<uint> col = slab.GetColumn<uint>(SectionType.GraphSizes);

        // Slice within one section → zero-copy span.
        ReadOnlySpan<uint> s = col.Slice(2, 4);
        Assert.Equal(new uint[] { 14, 21, 28, 35 }, s.ToArray());

        // A run spanning a section boundary is not sliceable (must CopyTo).
        Assert.False(col.TrySlice(6, 4, out _)); // 6..10 crosses the 8-element section boundary

        // CopyTo stitches across boundaries.
        var dest = new uint[10];
        col.CopyTo(5, dest);
        for (int i = 0; i < dest.Length; i++) Assert.Equal((uint)((5 + i) * 7), dest[i]);
    }

    [Fact]
    public void EmptyAndAbsentColumns()
    {
        using var slab = WriteSlab<ulong>(SectionType.GraphAddresses, 3, Array.Empty<ulong>(), sections: 1);
        Assert.Equal(0L, slab.GetColumn<ulong>(SectionType.GraphAddresses).Length); // empty column
        Assert.Equal(0L, slab.GetColumn<ulong>(SectionType.GraphSizes).Length);     // absent section
        Assert.False(slab.Has(SectionType.GraphSizes));
    }

    [Fact]
    public void WrongElementWidthThrows()
    {
        var recs = new ulong[] { 1, 2, 3 };
        using var slab = WriteSlab<ulong>(SectionType.GraphAddresses, 3, recs, sections: 1);
        // Section recordSize is 8 (ulong); reading as uint (4) must be rejected, not silently misread.
        Assert.Throws<InvalidDataException>(() => slab.GetColumn<uint>(SectionType.GraphAddresses));
    }

    [Fact]
    public void CorruptCountOverrunningBytesIsRejected()
    {
        // A hand-forged section whose declared count exceeds its byte length must be rejected on
        // GetColumn, not silently expose a too-long column that reads garbage past the section.
        var w = new ContainerWriter();
        w.AddSection(SectionType.GraphSizes, version: 3, recordSize: 4,
            new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 }, count: 100); // 8 bytes, but claims 100 uints
        using var slab = _tmp.WriteSlab(w);
        Assert.Throws<InvalidDataException>(() => slab.GetColumn<uint>(SectionType.GraphSizes));
    }

    [Fact]
    public void IndexOutOfRangeThrows()
    {
        var recs = new ulong[] { 1, 2, 3 };
        using var slab = WriteSlab<ulong>(SectionType.GraphAddresses, 3, recs, sections: 1);
        Column<ulong> col = slab.GetColumn<ulong>(SectionType.GraphAddresses);
        Assert.Throws<ArgumentOutOfRangeException>(() => col[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => col[-1]);
    }

    [Fact]
    public void SliceAtSectionEndIsEmptyNotOutOfBounds()
    {
        // A zero-length slice at the very end (offset == Length) must succeed, guarding the
        // ChunkedMmap.TryGetPointer end-of-file edge case.
        var recs = new uint[] { 1, 2, 3, 4 };
        using var slab = WriteSlab<uint>(SectionType.GraphSizes, 3, recs, sections: 1);
        Column<uint> col = slab.GetColumn<uint>(SectionType.GraphSizes);
        Assert.True(col.TrySlice(col.Length, 0, out ReadOnlySpan<uint> tail));
        Assert.Equal(0, tail.Length);
    }
}
