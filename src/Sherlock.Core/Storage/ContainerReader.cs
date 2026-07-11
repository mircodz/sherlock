using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sherlock.Core.Storage;

/// <summary>A parsed view of one section: metadata plus a zero-copy slice of the container bytes.</summary>
public readonly struct Section
{
    public SectionType Type { get; init; }
    public ushort Version { get; init; }
    public ushort RecordSize { get; init; }
    public ulong Count { get; init; }
    public ReadOnlyMemory<byte> Data { get; init; }

    /// <summary>Reinterprets a fixed-width section as <typeparamref name="T"/> records (zero-copy). Throws on a record-size mismatch.</summary>
    public ReadOnlySpan<T> AsRecords<T>() where T : struct
    {
        if (RecordSize != Unsafe.SizeOf<T>())
        {
            throw new InvalidDataException(
                $"section record size {RecordSize} != sizeof({typeof(T).Name}) {Unsafe.SizeOf<T>()}");
        }
        return MemoryMarshal.Cast<byte, T>(Data.Span);
    }
}

/// <summary>Reads a storage container; sections are zero-copy slices into the backing bytes (an array or an mmap view).</summary>
public sealed class ContainerReader : IDisposable
{
    private readonly List<Section> _sections = [];
    private readonly IDisposable? _backing; // the mmap owner, when opened from a file

    public ushort Version { get; }
    public IReadOnlyList<Section> Sections => _sections;

    public ContainerReader(ReadOnlyMemory<byte> bytes) : this(bytes, backing: null)
    {
    }

    private ContainerReader(ReadOnlyMemory<byte> bytes, IDisposable? backing)
    {
        _backing = backing;
        ReadOnlySpan<byte> span = bytes.Span;
        if (span.Length < ContainerFormat.HeaderSize)
        {
            throw new InvalidDataException("container smaller than its header");
        }
        if (!span[..4].SequenceEqual(ContainerFormat.Magic))
        {
            throw new InvalidDataException("bad container magic");
        }

        Version = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
        if ((flags & ContainerFormat.FlagLittleEndian) == 0)
        {
            throw new InvalidDataException("only little-endian containers are supported");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        long tableEnd = (long)ContainerFormat.HeaderSize + (long)count * ContainerFormat.SectionEntrySize;
        if (tableEnd > span.Length)
        {
            throw new InvalidDataException("section table exceeds container");
        }

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = span.Slice(
                ContainerFormat.HeaderSize + i * ContainerFormat.SectionEntrySize, ContainerFormat.SectionEntrySize);
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(e[16..]);
            if (offset > (ulong)span.Length || length > (ulong)span.Length || offset + length > (ulong)span.Length)
            {
                throw new InvalidDataException("section bounds exceed container");
            }

            _sections.Add(new Section
            {
                Type = (SectionType)BinaryPrimitives.ReadUInt32LittleEndian(e),
                Version = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]),
                RecordSize = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]),
                Count = BinaryPrimitives.ReadUInt64LittleEndian(e[24..]),
                Data = bytes.Slice((int)offset, (int)length),
            });
        }
    }

    /// <summary>
    /// Opens a container file memory-mapped (sections paged in on demand). The reader owns the
    /// mapping: dispose it when done, and don't retain section spans past disposal.
    /// </summary>
    public static ContainerReader Open(string path)
    {
        MemoryMappedRegion region = MemoryMappedRegion.Open(path);
        return new ContainerReader(region.Memory, region);
    }

    public void Dispose() => _backing?.Dispose();

    public bool TryGetSection(SectionType type, out Section section)
    {
        foreach (Section s in _sections)
        {
            if (s.Type == type)
            {
                section = s;
                return true;
            }
        }
        section = default;
        return false;
    }
}
