using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sherlock.Core.Storage;

/// <summary>
/// Reads a <c>.slab</c> container: open a file, ask for a section as a long-indexed
/// <see cref="Column{T}"/>. Hides memory-mapping, chunk boundaries, and the multi-section layout of
/// large columns; there is no ~2&nbsp;GB per-section ceiling. Owns one <see cref="ChunkedMmap"/>; every
/// column and blob it hands out is a zero-copy view valid until this <see cref="SlabFile"/> is disposed.
/// </summary>
public sealed class SlabFile : IDisposable
{
    // Descriptor only; columns resolve bytes lazily through the mmap, so a >2 GB section is fine.
    private readonly record struct SectionInfo(SectionType Type, ushort Version, ushort RecordSize, long ByteOffset, long ByteLength, long Count);

    private readonly ChunkedMmap _mmap;
    private readonly List<SectionInfo> _sections = [];

    public ushort Version { get; }

    private SlabFile(ChunkedMmap mmap)
    {
        _mmap = mmap;

        if (mmap.Length < ContainerFormat.HeaderSize)
        {
            throw new InvalidDataException("file smaller than container header (truncated)");
        }
        Span<byte> head = stackalloc byte[ContainerFormat.HeaderSize];
        mmap.CopyTo(0, head);
        if (!head[..4].SequenceEqual(ContainerFormat.Magic))
        {
            throw new InvalidDataException("bad container magic");
        }
        Version = BinaryPrimitives.ReadUInt16LittleEndian(head[4..]);
        if (Version != ContainerFormat.FormatVersion)
        {
            throw new InvalidDataException($"unsupported container version {Version}");
        }
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(head[6..]);
        if ((flags & ContainerFormat.FlagLittleEndian) == 0)
        {
            throw new InvalidDataException("only little-endian containers are supported");
        }
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);

        long tableEnd = ContainerFormat.HeaderSize + (long)count * ContainerFormat.SectionEntrySize;
        if (tableEnd > mmap.Length || tableEnd > int.MaxValue)
        {
            throw new InvalidDataException("section table exceeds container");
        }

        var table = new byte[checked((int)(count * (long)ContainerFormat.SectionEntrySize))];
        mmap.CopyTo(ContainerFormat.HeaderSize, table);
        var occupied = new List<(long Start, long End)>(checked((int)count));
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = table.AsSpan(i * ContainerFormat.SectionEntrySize, ContainerFormat.SectionEntrySize);
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(e[16..]);
            ulong records = BinaryPrimitives.ReadUInt64LittleEndian(e[24..]);
            ulong fileLength = (ulong)mmap.Length;
            if (offset > fileLength || length > fileLength - offset)
            {
                throw new InvalidDataException("section bounds exceed container");
            }
            if (length > 0 && offset < (ulong)tableEnd)
            {
                throw new InvalidDataException("section overlaps the container header");
            }
            if (records > long.MaxValue)
            {
                throw new InvalidDataException("section record count exceeds supported range");
            }

            if (length > 0)
            {
                occupied.Add(((long)offset, checked((long)(offset + length))));
            }
            _sections.Add(new SectionInfo(
                (SectionType)BinaryPrimitives.ReadUInt32LittleEndian(e),
                BinaryPrimitives.ReadUInt16LittleEndian(e[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(e[6..]),
                (long)offset,
                (long)length,
                (long)records));
        }

        occupied.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        for (int i = 1; i < occupied.Count; i++)
        {
            if (occupied[i].Start < occupied[i - 1].End)
            {
                throw new InvalidDataException("container sections overlap");
            }
        }
    }

    /// <summary>Opens (memory-maps) the slab at <paramref name="path"/>.</summary>
    public static SlabFile Open(string path)
    {
        ChunkedMmap mmap = ChunkedMmap.Open(path);
        try
        {
            return new SlabFile(mmap);
        }
        catch
        {
            mmap.Dispose();
            throw;
        }
    }

    public bool Has(SectionType type)
    {
        foreach (SectionInfo s in _sections)
        {
            if (s.Type == type)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The version of the first section of <paramref name="type"/>, or 0 if absent.</summary>
    public ushort SectionVersion(SectionType type)
    {
        foreach (SectionInfo s in _sections)
        {
            if (s.Type == type)
            {
                return s.Version;
            }
        }
        return 0;
    }

    /// <summary>A section as a long-indexed column, reassembling every same-typed section into one
    /// logical column (a large column is written as several).</summary>
    public Column<T> GetColumn<T>(SectionType type) where T : unmanaged
    {
        int width = Unsafe.SizeOf<T>();
        var segs = new List<(long byteOffset, long count)>();
        foreach (SectionInfo s in _sections)
        {
            if (s.Type != type)
            {
                continue;
            }

            Validate<T>(type, s, width);
            segs.Add((s.ByteOffset, s.Count));
        }
        return segs.Count == 0
            ? Column<T>.Empty
            : new Column<T>(_mmap, CollectionsMarshal.AsSpan(segs));
    }

    /// <summary>Each same-typed section as its own column, in table order; unlike
    /// <see cref="GetColumn{T}"/>, kept separate (the heap-graph edge chunks feeding <c>EdgeColumn</c>).</summary>
    public IReadOnlyList<Column<T>> SectionColumns<T>(SectionType type) where T : unmanaged
    {
        int width = Unsafe.SizeOf<T>();
        var result = new List<Column<T>>();
        foreach (SectionInfo s in _sections)
        {
            if (s.Type != type)
            {
                continue;
            }

            Validate<T>(type, s, width);
            result.Add(new Column<T>(_mmap, [(s.ByteOffset, s.Count)]));
        }
        return result;
    }

    // Guards a typed section against a corrupt/mismatched descriptor before it's exposed as a column:
    // a wrong record size or a Count that overruns the section's bytes would otherwise read garbage.
    private static void Validate<T>(SectionType type, SectionInfo s, int width) where T : unmanaged
    {
        if (s.RecordSize != width)
        {
            throw new InvalidDataException($"section {type} record size {s.RecordSize} != sizeof({typeof(T).Name}) {width}");
        }
        if (checked(s.Count * width) > s.ByteLength)
        {
            throw new InvalidDataException($"section {type} count {s.Count} overruns its {s.ByteLength} bytes");
        }
    }

    /// <summary>Copies a whole single-section blob into an owned array (variable-width sections: the
    /// string pool, the type-name blob).</summary>
    public byte[] Blob(SectionType type)
    {
        foreach (SectionInfo s in _sections)
        {
            if (s.Type != type)
            {
                continue;
            }

            var buf = new byte[checked((int)s.ByteLength)];
            _mmap.CopyTo(s.ByteOffset, buf);
            return buf;
        }
        return [];
    }

    public void Dispose() => _mmap.Dispose();
}
