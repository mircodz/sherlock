using System;
using System.IO;
using System.Runtime.InteropServices;
using Sherlock.Core.Storage;
using Sherlock.Core.Tests.Common;
using Xunit;

namespace Sherlock.Core.Tests.Storage;

public class ContainerTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    // Shared fixture: src/native/tests/container.cpp, Container.GoldenBytesMatchSpec.
    private static readonly byte[] GoldenHead =
    [
        0x53, 0x48, 0x52, 0x4B,             // "SHRK"
        0x02, 0x00,                         // container version = 2
        0x01, 0x00,                         // kind = Profile
        0x07, 0x00, 0x00, 0x00,             // kind version = 7
        0x01, 0x00, 0x00, 0x00,             // section count = 1
        0x01, 0x00, 0x00, 0x00,             // type = Strings
        0x01, 0x00, 0x00, 0x00,             // record size = 1
        0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // offset = 4096
        0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // length = 4
    ];

    private static byte[] GoldenBytes()
    {
        var bytes = new byte[ContainerFormat.Alignment + 4];
        GoldenHead.CopyTo(bytes, 0);
        new byte[] { 1, 2, 3, 4 }.CopyTo(bytes, ContainerFormat.Alignment);
        return bytes;
    }

    private string Write(byte[] bytes)
    {
        string path = _tmp.File();
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void WriterProducesGoldenBytes()
    {
        var w = new ContainerWriter(SlabKind.Profile, 7);
        w.AddBytes(SectionType.Strings, [1, 2, 3, 4]);
        Assert.Equal(GoldenBytes(), w.ToArray());
    }

    [Fact]
    public void ReaderParsesGoldenBytes()
    {
        using var r = SlabFile.Open(Write(GoldenBytes()), SlabKind.Profile, 7);
        Assert.True(r.Has(SectionType.Strings));
        Assert.False(r.Has(SectionType.Types));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, r.Blob(SectionType.Strings));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rec
    {
        public uint A;
        public ulong B;
    }

    [Fact]
    public void RoundTripsARecordSection()
    {
        var w = new ContainerWriter(SlabKind.Profile, 1);
        w.AddRecords(SectionType.Allocations, new[] { new Rec { A = 1, B = 2 }, new Rec { A = 3, B = 4 } });

        using var r = _tmp.WriteSlab(w, SlabKind.Profile, 1);
        Column<Rec> got = r.GetColumn<Rec>(SectionType.Allocations);
        Assert.Equal(2L, got.Length);
        Assert.Equal(1u, got[0].A);
        Assert.Equal(4ul, got[1].B);
    }

    [Fact]
    public void WritesOneSectionFromSeveralParts()
    {
        var w = new ContainerWriter(SlabKind.Graph, 1);
        w.AddMemory<int>(SectionType.GraphEdges, [new[] { 1, 2 }, Array.Empty<int>(), new[] { 3, 4, 5 }]);

        using var r = _tmp.WriteSlab(w, SlabKind.Graph, 1);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, r.GetColumn<int>(SectionType.GraphEdges).AsMemory().ToArray());
    }

    [Fact]
    public void GetColumnRejectsAnotherRecordSize()
    {
        var w = new ContainerWriter(SlabKind.Profile, 1);
        w.AddRecords(SectionType.Correlation, new uint[] { 0 });
        using var r = _tmp.WriteSlab(w, SlabKind.Profile, 1);
        Assert.Throws<InvalidDataException>(() => r.GetColumn<Rec>(SectionType.Correlation));
    }

    [Fact]
    public void AbsentSectionsReadAsEmpty()
    {
        using var r = _tmp.WriteSlab(new ContainerWriter(SlabKind.Profile, 1), SlabKind.Profile, 1);
        Assert.Equal(0L, r.GetColumn<ulong>(SectionType.Correlation).Length);
        Assert.Empty(r.Blob(SectionType.Strings));
    }

    [Fact]
    public void AnEmptyContainerIsJustTheHeader()
    {
        Assert.Equal(ContainerFormat.HeaderSize, new ContainerWriter(SlabKind.Profile, 1).ToArray().Length);
    }

    [Fact]
    public void SectionsStartOnPageBoundaries()
    {
        var w = new ContainerWriter(SlabKind.Profile, 1);
        w.AddBytes(SectionType.Strings, [1, 2, 3]);
        w.AddBytes(SectionType.Types, [4, 5, 6]);
        byte[] bytes = w.ToArray();

        Assert.Equal(ContainerFormat.Alignment, BitConverter.ToInt64(bytes, 16 + 8));
        Assert.Equal(2 * ContainerFormat.Alignment, BitConverter.ToInt64(bytes, 16 + 24 + 8));
        using var r = SlabFile.Open(Write(bytes), SlabKind.Profile, 1);
        Assert.Equal(new byte[] { 1, 2, 3 }, r.Blob(SectionType.Strings));
        Assert.Equal(new byte[] { 4, 5, 6 }, r.Blob(SectionType.Types));
    }

    [Fact]
    public void WriterRejectsASectionAddedTwice()
    {
        var w = new ContainerWriter(SlabKind.Profile, 1);
        w.AddBytes(SectionType.Strings, [1]);
        Assert.Throws<InvalidOperationException>(() => w.AddBytes(SectionType.Strings, [2]));
    }

    [Fact]
    public void RejectsBadMagicAndTruncation()
    {
        Assert.Throws<InvalidDataException>(() => SlabFile.Open(Write(new byte[16]), SlabKind.Profile, 7));
        Assert.Throws<InvalidDataException>(() => SlabFile.Open(Write([0x53, 0x48, 0x52]), SlabKind.Profile, 7));
    }

    [Theory]
    [InlineData(4, 1)] // container version 1
    [InlineData(8, 8)] // kind version 8
    public void RejectsOtherVersions(int field, byte value)
    {
        byte[] bytes = GoldenBytes();
        bytes[field] = value;
        Assert.Contains("incompatible version", Assert.Throws<InvalidDataException>(() => SlabFile.Open(Write(bytes), SlabKind.Profile, 7)).Message);
    }

    [Fact]
    public void RejectsAnotherKind()
    {
        Assert.Contains("holds an allocation profile, not a heap graph",
            Assert.Throws<InvalidDataException>(() => SlabFile.Open(Write(GoldenBytes()), SlabKind.Graph, 7)).Message);
    }

    [Fact]
    public void RejectsASectionPastTheEnd()
    {
        byte[] bytes = GoldenBytes();
        bytes[25] = 0x20; // offset 0x2000, past the 4100-byte file
        Assert.Contains("past the end", Assert.Throws<InvalidDataException>(() => SlabFile.Open(Write(bytes), SlabKind.Profile, 7)).Message);
    }

    [Fact]
    public void RejectsAMisalignedSection()
    {
        byte[] bytes = [.. GoldenBytes(), 0];
        bytes[24] = 0x01; // offset 0x1001, still inside the file
        Assert.Contains("aligned", Assert.Throws<InvalidDataException>(() => SlabFile.Open(Write(bytes), SlabKind.Profile, 7)).Message);
    }

    [Fact]
    public void RejectsARecordSizeOfZero()
    {
        byte[] bytes = GoldenBytes();
        bytes[20] = 0;
        Assert.Contains("whole number", Assert.Throws<InvalidDataException>(() => SlabFile.Open(Write(bytes), SlabKind.Profile, 7)).Message);
    }

    [Fact]
    public void RejectsASectionAppearingTwice()
    {
        var w = new ContainerWriter(SlabKind.Profile, 7);
        w.AddBytes(SectionType.Strings, [1]);
        w.AddBytes(SectionType.Types, [2]);
        byte[] bytes = w.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(16 + 24), (uint)SectionType.Strings);

        Assert.Contains("twice", Assert.Throws<InvalidDataException>(() => SlabFile.Open(Write(bytes), SlabKind.Profile, 7)).Message);
    }

    [Fact]
    public void RejectsOverlappingSections()
    {
        var w = new ContainerWriter(SlabKind.Profile, 7);
        w.AddBytes(SectionType.Strings, new byte[5000]);
        w.AddBytes(SectionType.Types, [2]);
        byte[] bytes = w.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(16 + 24 + 8), (ulong)ContainerFormat.Alignment); // Types starts inside Strings

        Assert.Contains("overlap", Assert.Throws<InvalidDataException>(() => SlabFile.Open(Write(bytes), SlabKind.Profile, 7)).Message);
    }

    [Fact]
    public void SaveAtomicallyReplacesTheFile()
    {
        string path = _tmp.File();
        File.WriteAllText(path, "old");
        var writer = new ContainerWriter(SlabKind.Graph, 1);
        writer.AddRecords(SectionType.GraphSizes, new uint[] { 1, 2, 3 });

        writer.Save(path);

        using var slab = SlabFile.Open(path, SlabKind.Graph, 1);
        Assert.Equal(new uint[] { 1, 2, 3 }, slab.GetColumn<uint>(SectionType.GraphSizes).AsMemory().ToArray());
        Assert.Empty(Directory.EnumerateFiles(_tmp.Path, "*.tmp"));
    }
}
