using System;
using System.IO;
using System.Runtime.InteropServices;
using Sherlock.Core.Storage;

namespace Sherlock.Core.Tests.Storage;

public class ContainerTests
{
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
        var r = new ContainerReader(GoldenBytes);
        Assert.Equal(ContainerFormat.FormatVersion, r.Version);
        Assert.True(r.TryGetSection(SectionType.Frames, out Section s));
        Assert.Equal((ushort)1, s.Version);
        Assert.Equal((ushort)0, s.RecordSize);
        Assert.Equal(2ul, s.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, s.Data.ToArray());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rec
    {
        public uint A;
        public ulong B;
    }

    [Fact]
    public void RoundTrips_RecordSection_ZeroCopy()
    {
        var recs = new[] { new Rec { A = 1, B = 2 }, new Rec { A = 3, B = 4 } };
        var w = new ContainerWriter();
        w.AddRecords(SectionType.Allocations, version: 3, recs);

        var r = new ContainerReader(w.ToArray());
        Assert.True(r.TryGetSection(SectionType.Allocations, out Section s));
        Assert.Equal((ushort)3, s.Version);

        ReadOnlySpan<Rec> got = s.AsRecords<Rec>();
        Assert.Equal(2, got.Length);
        Assert.Equal(1u, got[0].A);
        Assert.Equal(4ul, got[1].B);
    }

    [Fact]
    public void AsRecords_Throws_OnSizeMismatch()
    {
        var w = new ContainerWriter();
        w.AddSection(SectionType.Correlation, version: 1, recordSize: 4, new byte[] { 0, 0, 0, 0 }, count: 1);
        var r = new ContainerReader(w.ToArray());
        Assert.True(r.TryGetSection(SectionType.Correlation, out Section s));
        Assert.Throws<InvalidDataException>(() => s.AsRecords<Rec>().Length);
    }

    [Fact]
    public void Empty_Container_IsJustHeader()
    {
        byte[] bytes = new ContainerWriter().ToArray();
        Assert.Equal(ContainerFormat.HeaderSize, bytes.Length);
        var r = new ContainerReader(bytes);
        Assert.Empty(r.Sections);
    }

    [Fact]
    public void OddSizedSections_RoundTrip_WithAlignmentPadding()
    {
        // 3-byte sections force the writer to pad so the next section stays 8-aligned; the data
        // must still come back exactly, which only holds if offsets/lengths are computed right.
        var w = new ContainerWriter();
        w.AddSection(SectionType.Strings, 1, 0, new byte[] { 1, 2, 3 }, 3);
        w.AddSection(SectionType.Frames, 1, 0, new byte[] { 4, 5, 6 }, 3);

        var r = new ContainerReader(w.ToArray());
        Assert.True(r.TryGetSection(SectionType.Strings, out Section a));
        Assert.True(r.TryGetSection(SectionType.Frames, out Section b));
        Assert.Equal(new byte[] { 1, 2, 3 }, a.Data.ToArray());
        Assert.Equal(new byte[] { 4, 5, 6 }, b.Data.ToArray());
    }

    [Fact]
    public void Rejects_BadMagic()
    {
        byte[] bad = new byte[16];
        Assert.Throws<InvalidDataException>(() => new ContainerReader(bad));
    }

    [Fact]
    public void Rejects_Truncation()
    {
        Assert.Throws<InvalidDataException>(() => new ContainerReader(new byte[] { 0x53, 0x48, 0x52 }));
    }

    [Fact]
    public void Open_MemoryMapsAFile_AndReadsZeroCopy()
    {
        var w = new ContainerWriter();
        w.AddSection(SectionType.Frames, 1, 0, new byte[] { 9, 8, 7, 6 }, 4);
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, w.ToArray());
            using (ContainerReader r = ContainerReader.Open(path))
            {
                Assert.True(r.TryGetSection(SectionType.Frames, out Section s));
                Assert.Equal(new byte[] { 9, 8, 7, 6 }, s.Data.ToArray());
            }
            // After disposal the mapping is released and the file is no longer locked.
            File.Delete(path);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
