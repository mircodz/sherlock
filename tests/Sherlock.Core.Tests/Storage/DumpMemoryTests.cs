using System;
using System.IO;
using System.Runtime.InteropServices;
using Sherlock.Core.Storage;
using Sherlock.Core.Tests.Common;
using Xunit;

namespace Sherlock.Core.Tests.Storage;

public sealed unsafe class DumpMemoryTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void MachOCoreJoinsRunsContiguousInMemoryAndFile()
    {
        const int Data = 4096;
        var file = new byte[Data + 0x400];
        Put(file, 0, 0xfeedfacfu);
        Put(file, 12, 4u); // MH_CORE
        Put(file, 16, 5u); // commands
        int at = 32;
        at = Segment(file, at, 0x10000, 0x100, Data, 0x100);
        at = Segment(file, at, 0x10100, 0x100, Data + 0x100, 0x100);         // continues the first in memory and file
        at = Command(file, at, 0x4, 24);                                      // LC_THREAD, skipped
        at = Segment(file, at, 0x10200, 0x80, Data + 0x300, 0x80);           // next in memory, not in the file
        Segment(file, at, 0x20000, 0x40, Data + 0x380, 0x20);                // only half captured
        Fill(file, Data, 0x10000, 0x200);
        Fill(file, Data + 0x300, 0x10200, 0x80);
        Fill(file, Data + 0x380, 0x20000, 0x20);
        using DumpMemory memory = Open(file);

        byte* run = memory.Pointer(0x10000, 0x200, out ulong limit);
        Assert.True(run != null);
        Assert.Equal(0x10200UL, limit);
        AssertPattern(new ReadOnlySpan<byte>(run, 0x200), 0x10000);
        Assert.True(memory.Pointer(0x10180, 0x100, out _) == null);
        var across = new byte[0x100];
        Assert.True(memory.TryCopy(0x10180, across));
        AssertPattern(across, 0x10180);
        Assert.True(memory.Pointer(0x20000, 0x20, out _) != null);
        Assert.True(memory.Pointer(0x20000, 0x21, out _) == null);
        Assert.True(memory.Pointer(0x20020, 1, out _) == null);
        Assert.False(memory.TryCopy(0x1fff0, new byte[0x20]));
        memory.WillNeed(0x10000, 0x40000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ElfCoreReadsLoadSegmentsUpToTheirFileSize(bool extendedCount)
    {
        const int Data = 4096;
        var file = new byte[Data + 0x180];
        "\u007fELF"u8.CopyTo(file);
        file[4] = 2; // 64-bit
        file[5] = 1; // little-endian
        Put(file, 0x10, (ushort)4); // ET_CORE
        Put(file, 0x20, 64UL);      // program headers
        Put(file, 0x36, (ushort)56);
        Put(file, 0x38, extendedCount ? (ushort)0xffff : (ushort)3);
        if (extendedCount)
        {
            Put(file, 0x28, 1024UL); // section header 0 holds the count in sh_info
            Put(file, 1024 + 44, 3u);
        }
        Header(file, 64, 4, 0, 0, 0);                         // PT_NOTE, skipped
        Header(file, 120, 1, Data, 0x7000, 0x100);            // PT_LOAD
        Header(file, 176, 1, Data + 0x100, 0x9000, 0x80);     // PT_LOAD
        Fill(file, Data, 0x7000, 0x100);
        Fill(file, Data + 0x100, 0x9000, 0x80);
        using DumpMemory memory = Open(file);

        AssertPattern(new ReadOnlySpan<byte>(memory.Pointer(0x7000, 0x100, out _), 0x100), 0x7000);
        Assert.True(memory.Pointer(0x7000, 0x101, out _) == null);
        AssertPattern(new ReadOnlySpan<byte>(memory.Pointer(0x9010, 0x70, out _), 0x70), 0x9010);

        static void Header(byte[] file, int at, uint type, ulong offset, ulong address, ulong size)
        {
            Put(file, at, type);
            Put(file, at + 8, offset);
            Put(file, at + 16, address);
            Put(file, at + 32, size);       // p_filesz
            Put(file, at + 40, size * 2);   // p_memsz: the rest isn't in the file
        }
    }

    [Fact]
    public void MinidumpReadsBothMemoryLists()
    {
        const int Data = 4096;
        var file = new byte[Data + 0x200];
        Put(file, 0, 0x504d444du); // MDMP
        Put(file, 8, 3u);          // streams
        Put(file, 12, 32u);        // directory
        Stream(file, 32, 3, 512);  // a thread list, skipped
        Stream(file, 44, 9, 256);  // Memory64ListStream
        Stream(file, 56, 5, 384);  // MemoryListStream
        Put(file, 256, 2UL);
        Put(file, 264, (ulong)Data);
        Put(file, 272, 0xA000UL);
        Put(file, 280, 0x100UL);
        Put(file, 288, 0xB000UL);
        Put(file, 296, 0x80UL);
        Put(file, 384, 1u);
        Put(file, 388, 0xC000UL);
        Put(file, 396, 0x40u);
        Put(file, 400, (uint)(Data + 0x180));
        Fill(file, Data, 0xA000, 0x100);
        Fill(file, Data + 0x100, 0xB000, 0x80);
        Fill(file, Data + 0x180, 0xC000, 0x40);
        using DumpMemory memory = Open(file);

        AssertPattern(new ReadOnlySpan<byte>(memory.Pointer(0xA000, 0x100, out _), 0x100), 0xA000);
        AssertPattern(new ReadOnlySpan<byte>(memory.Pointer(0xB000, 0x80, out _), 0x80), 0xB000);
        AssertPattern(new ReadOnlySpan<byte>(memory.Pointer(0xC000, 0x40, out _), 0x40), 0xC000);
        Assert.True(memory.Pointer(0xA0FF, 2, out _) == null);

        static void Stream(byte[] file, int at, uint type, uint rva)
        {
            Put(file, at, type);
            Put(file, at + 8, rva);
        }
    }

    [Fact]
    public void CapturedCountsTheBytesBeforeTheFirstGap()
    {
        const int Data = 4096;
        var file = new byte[Data + 0x400];
        Put(file, 0, 0xfeedfacfu);
        Put(file, 16, 3u);
        int at = Segment(file, 32, 0x10000, 0x200, Data, 0x200);
        at = Segment(file, at, 0x10200, 0x80, Data + 0x300, 0x80); // next in memory, elsewhere in the file
        Segment(file, at, 0x20000, 0x40, Data + 0x380, 0x20);       // only half captured
        using DumpMemory memory = Open(file);

        Assert.Equal(0x280UL, memory.Captured(0x10000, 0x300));
        Assert.Equal(0x100UL, memory.Captured(0x10100, 0x100));
        Assert.Equal(0x10UL, memory.Captured(0x20010, 0x40));
        Assert.Equal(0UL, memory.Captured(0x30000, 8));
    }

    [Fact]
    public void RangeCutShortByTheEndOfTheFileKeepsWhatTheFileHas()
    {
        var file = new byte[4096 + 0x80];
        Put(file, 0, 0xfeedfacfu);
        Put(file, 16, 1u);
        Segment(file, 32, 0x10000, 0x100, 4096, 0x100);
        Fill(file, 4096, 0x10000, 0x80);
        using DumpMemory memory = Open(file);

        Assert.True(memory.Pointer(0x10000, 0x80, out _) != null);
        Assert.True(memory.Pointer(0x10000, 0x81, out _) == null);
    }

    [Fact]
    public void HeadersPointingPastTheEndAreRejected()
    {
        var file = new byte[36];
        Put(file, 0, 0xfeedfacfu);
        Put(file, 16, 3u); // three commands, but the file ends inside the first

        var error = Assert.Throws<InvalidDataException>(() => Open(file));
        Assert.Contains("past its end", error.Message);
    }

    [Fact]
    public void UnknownFormatsAreRejected()
    {
        var error = Assert.Throws<InvalidDataException>(() => Open(new byte[64]));
        Assert.Contains("magic 0x00000000", error.Message);
    }

    private DumpMemory Open(byte[] bytes)
    {
        string path = _tmp.File(".dmp");
        File.WriteAllBytes(path, bytes);
        return DumpMemory.Open(path);
    }

    private static int Segment(byte[] file, int at, ulong address, ulong memorySize, int fileOffset, ulong fileSize)
    {
        Put(file, at, 0x19u); // LC_SEGMENT_64
        Put(file, at + 4, 72u);
        Put(file, at + 24, address);
        Put(file, at + 32, memorySize);
        Put(file, at + 40, (ulong)fileOffset);
        Put(file, at + 48, fileSize);
        return at + 72;
    }

    private static int Command(byte[] file, int at, uint command, uint size)
    {
        Put(file, at, command);
        Put(file, at + 4, size);
        return at + (int)size;
    }

    private static void Put<T>(byte[] file, int at, T value) where T : unmanaged => MemoryMarshal.Write(file.AsSpan(at), in value);

    // Each captured byte encodes its address, so a read shows where it came from.
    private static byte Pattern(ulong address) => (byte)(address ^ (address >> 8));

    private static void Fill(byte[] file, int at, ulong address, int count)
    {
        for (int i = 0; i < count; i++)
        {
            file[at + i] = Pattern(address + (ulong)i);
        }
    }

    private static void AssertPattern(ReadOnlySpan<byte> bytes, ulong address)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            Assert.Equal(Pattern(address + (ulong)i), bytes[i]);
        }
    }
}
