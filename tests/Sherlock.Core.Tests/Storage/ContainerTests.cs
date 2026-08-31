using System;
using System.IO;
using System.Runtime.InteropServices;
using Sherlock.Core.Storage;
using Sherlock.Core.Tests.Common;
using Xunit;

namespace Sherlock.Core.Tests.Storage;

// Container-format tests. The writer's byte layout is pinned by the GoldenBytes fixture; reading is
// exercised through SlabFile (the memory-mapped reader we actually use) + Column<T>.
public class ContainerTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private SlabFile Open(ContainerWriter w) => _tmp.WriteSlab(w);

    // The canonical cross-language fixture: one Frames section, version 1, blob (recordSize 0),
    // count 2, data {1,2,3,4}. The exact same expected bytes are asserted in the native test
    // (src/native/tests/container.cpp, Container.GoldenBytesMatchSpec) — if either side's layout
    // drifts, one of the two golden tests fails.
    private static readonly byte[] GoldenBytes =
    [
        // header (16)
        0x53, 0x48, 0x52, 0x4B,             // "SHRK"
        0x01, 0x00,                         // formatVersion = 1
        0x01, 0x00,                         // flags = little-endian
        0x01, 0x00, 0x00, 0x00,             // sectionCount = 1
        0x00, 0x00, 0x00, 0x00,             // reserved
        // section entry (32)
        0x02, 0x00, 0x00, 0x00,             // type = Frames(2)
        0x01, 0x00,                         // version = 1
        0x00, 0x00,                         // recordSize = 0
        0x30, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // offset = 48
        0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // length = 4
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // count = 2
        // data (4)
        0x01, 0x02, 0x03, 0x04,
    ];

    [Fact]
    public void Writer_ProducesGoldenBytes()
    {
        var w = new ContainerWriter();
        w.AddSection(SectionType.Frames, version: 1, recordSize: 0, new byte[] { 1, 2, 3, 4 }, count: 2);
        Assert.Equal(GoldenBytes, w.ToArray());
    }

    [Fact]
    public void Reader_ParsesGoldenBytes()
    {
        string path = Path.Combine(_tmp.Path, "golden.slab");
        File.WriteAllBytes(path, GoldenBytes);
        using var r = SlabFile.Open(path);
        Assert.Equal(ContainerFormat.FormatVersion, r.Version);
        Assert.True(r.Has(SectionType.Frames));
        Assert.Equal((ushort)1, r.SectionVersion(SectionType.Frames));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, r.Blob(SectionType.Frames));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rec
    {
        public uint A;
        public ulong B;
    }

    [Fact]
    public void RoundTrips_RecordSection()
    {
        var recs = new[] { new Rec { A = 1, B = 2 }, new Rec { A = 3, B = 4 } };
        var w = new ContainerWriter();
        w.AddRecords(SectionType.Allocations, version: 3, recs);

        using var r = Open(w);
        Assert.Equal((ushort)3, r.SectionVersion(SectionType.Allocations));
        Column<Rec> got = r.GetColumn<Rec>(SectionType.Allocations);
        Assert.Equal(2L, got.Length);
        Assert.Equal(1u, got[0].A);
        Assert.Equal(4ul, got[1].B);
    }

    [Fact]
    public void GetColumn_Throws_OnSizeMismatch()
    {
        var w = new ContainerWriter();
        w.AddSection(SectionType.Correlation, version: 1, recordSize: 4, new byte[] { 0, 0, 0, 0 }, count: 1);
        using var r = Open(w);
        Assert.Throws<InvalidDataException>(() => r.GetColumn<Rec>(SectionType.Correlation));
    }

    [Fact]
    public void Empty_Container_IsJustHeader()
    {
        byte[] bytes = new ContainerWriter().ToArray();
        Assert.Equal(ContainerFormat.HeaderSize, bytes.Length);
    }

    [Fact]
    public void OddSizedSections_RoundTrip_WithAlignmentPadding()
    {
        // 3-byte sections force the writer to pad so the next section stays 8-aligned; the data
        // must still come back exactly, which only holds if offsets/lengths are computed right.
        var w = new ContainerWriter();
        w.AddSection(SectionType.Strings, 1, 0, new byte[] { 1, 2, 3 }, 3);
        w.AddSection(SectionType.Frames, 1, 0, new byte[] { 4, 5, 6 }, 3);

        using var r = Open(w);
        Assert.Equal(new byte[] { 1, 2, 3 }, r.Blob(SectionType.Strings));
        Assert.Equal(new byte[] { 4, 5, 6 }, r.Blob(SectionType.Frames));
    }

    [Fact]
    public void Rejects_BadMagic()
    {
        string path = Path.Combine(_tmp.Path, "bad.slab");
        File.WriteAllBytes(path, new byte[16]);
        Assert.Throws<InvalidDataException>(() => SlabFile.Open(path));
    }

    [Fact]
    public void Rejects_Truncation()
    {
        string path = Path.Combine(_tmp.Path, "trunc.slab");
        File.WriteAllBytes(path, new byte[] { 0x53, 0x48, 0x52 });
        Assert.Throws<InvalidDataException>(() => SlabFile.Open(path));
    }

    [Fact]
    public void Rejects_UnsupportedContainerVersion()
    {
        byte[] bytes = (byte[])GoldenBytes.Clone();
        bytes[4] = 2;
        string path = Path.Combine(_tmp.Path, "future.slab");
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => SlabFile.Open(path));
    }

    [Fact]
    public void Rejects_OverflowingSectionBounds()
    {
        byte[] bytes = (byte[])GoldenBytes.Clone();
        bytes.AsSpan(24, sizeof(ulong)).Fill(0xff); // section offset = ulong.MaxValue
        string path = Path.Combine(_tmp.Path, "overflow.slab");
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => SlabFile.Open(path));
    }

    [Fact]
    public void Open_MemoryMapsAFile_AndReadsZeroCopy()
    {
        var w = new ContainerWriter();
        w.AddRecords(SectionType.Frames, 1, new uint[] { 9, 8, 7, 6 });
        using (var r = Open(w))
        {
            Column<uint> col = r.GetColumn<uint>(SectionType.Frames);
            Assert.Equal(new uint[] { 9, 8, 7, 6 }, col.AsMemory().ToArray());
        }
    }

    [Fact]
    public void SaveAtomicallyReplacesTheFile()
    {
        string path = _tmp.File();
        File.WriteAllText(path, "old");
        var writer = new ContainerWriter();
        writer.AddRecords(SectionType.GraphSizes, 1, new uint[] { 1, 2, 3 });

        writer.Save(path);

        using var slab = SlabFile.Open(path);
        Assert.Equal(new uint[] { 1, 2, 3 }, slab.GetColumn<uint>(SectionType.GraphSizes).AsMemory().ToArray());
        Assert.Empty(Directory.EnumerateFiles(_tmp.Path, "*.tmp"));
    }
}
