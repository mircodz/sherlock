using System;
using System.Buffers;
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

    /// <summary>The section reinterpreted as a <typeparamref name="T"/> <see cref="ReadOnlyMemory{T}"/>
    /// (zero-copy — a view over the same backing bytes, so it stays valid only as long as the container
    /// reader that produced this section is alive). Throws on a record-size mismatch.</summary>
    public ReadOnlyMemory<T> AsMemory<T>() where T : struct
    {
        if (RecordSize != Unsafe.SizeOf<T>())
        {
            throw new InvalidDataException(
                $"section record size {RecordSize} != sizeof({typeof(T).Name}) {Unsafe.SizeOf<T>()}");
        }
        if (Data.Length == 0)
        {
            return ReadOnlyMemory<T>.Empty;
        }
        return new CastMemoryManager<T>(Data).Memory;
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
    /// mapping: dispose it when done, and don't retain section spans past disposal. Files larger than
    /// 2&nbsp;GiB are supported via chunked views; a section that itself straddles a chunk boundary is
    /// copied into a private buffer (rare — sections are 8-aligned and typically well under a chunk).
    /// </summary>
    public static ContainerReader Open(string path)
    {
        var mmap = ChunkedMmap.Open(path);
        try
        {
            return new ContainerReader(mmap);
        }
        catch
        {
            mmap.Dispose();
            throw;
        }
    }

    // Builds the reader from a chunked mmap: header + table are read into a small buffer, and each
    // section is exposed zero-copy when it fits in one chunk, else copied.
    private ContainerReader(ChunkedMmap mmap)
    {
        _backing = mmap;

        Span<byte> head = stackalloc byte[ContainerFormat.HeaderSize];
        mmap.CopyTo(0, head);
        if (!head[..4].SequenceEqual(ContainerFormat.Magic))
        {
            throw new InvalidDataException("bad container magic");
        }
        Version = BinaryPrimitives.ReadUInt16LittleEndian(head[4..]);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(head[6..]);
        if ((flags & ContainerFormat.FlagLittleEndian) == 0)
        {
            throw new InvalidDataException("only little-endian containers are supported");
        }
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);

        long tableEnd = (long)ContainerFormat.HeaderSize + (long)count * ContainerFormat.SectionEntrySize;
        if (tableEnd > mmap.Length)
        {
            throw new InvalidDataException("section table exceeds container");
        }

        var table = new byte[count * ContainerFormat.SectionEntrySize];
        mmap.CopyTo(ContainerFormat.HeaderSize, table);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = table.AsSpan(i * ContainerFormat.SectionEntrySize, ContainerFormat.SectionEntrySize);
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(e[8..]);
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(e[16..]);
            if (offset > (ulong)mmap.Length || length > (ulong)mmap.Length || offset + length > (ulong)mmap.Length)
            {
                throw new InvalidDataException("section bounds exceed container");
            }

            _sections.Add(new Section
            {
                Type = (SectionType)BinaryPrimitives.ReadUInt32LittleEndian(e),
                Version = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]),
                RecordSize = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]),
                Count = BinaryPrimitives.ReadUInt64LittleEndian(e[24..]),
                Data = ViewSection(mmap, (long)offset, (long)length),
            });
        }
    }

    // A section's bytes: zero-copy when it lies within one chunk, else copied into a private buffer. A
    // single section is capped at int.MaxValue bytes (ReadOnlyMemory is int-length-bounded); the writer
    // enforces this by splitting huge columns (graph edges) into multiple ≤~1 GB sections.
    private static unsafe ReadOnlyMemory<byte> ViewSection(ChunkedMmap mmap, long offset, long length)
    {
        if (length == 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }
        if (length > int.MaxValue)
        {
            throw new InvalidDataException($"section is {length} bytes (> 2 GB); columns this large must be written as multiple chunked sections.");
        }
        int len = (int)length;
        if (mmap.TryGetPointer(offset, len, out nint ptr))
        {
            return new ChunkPointerMemory((byte*)ptr, len).Memory;
        }
        var copy = new byte[len];
        mmap.CopyTo(offset, copy);
        return copy;
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

    /// <summary>All sections of <paramref name="type"/>, in table order. Used where a logical column is
    /// split across several same-typed sections (the heap-graph edge chunks).</summary>
    public IReadOnlyList<Section> GetSections(SectionType type)
    {
        var matches = new List<Section>();
        foreach (Section s in _sections)
        {
            if (s.Type == type) matches.Add(s);
        }
        return matches;
    }
}

/// <summary>Exposes a range of a memory-mapped chunk (a raw pointer) as <see cref="ReadOnlyMemory{Byte}"/>,
/// zero-copy. The pointer must outlive every <c>Memory</c>/<c>Span</c> handed out — the owning
/// <see cref="ChunkedMmap"/> (held by the reader) guarantees this until the reader is disposed.</summary>
internal sealed unsafe class ChunkPointerMemory(byte* pointer, int length) : MemoryManager<byte>
{
    private readonly byte* _pointer = pointer;
    private readonly int _length = length;

    public override Span<byte> GetSpan() => new(_pointer, _length);
    public override MemoryHandle Pin(int elementIndex = 0) => new(_pointer + elementIndex);
    public override void Unpin() { }
    protected override void Dispose(bool disposing) { } // the mmap owns the memory, not this manager
}

/// <summary>Reinterprets a byte <see cref="ReadOnlyMemory{Byte}"/> as a <typeparamref name="T"/> memory,
/// zero-copy — there is no built-in <c>Memory</c> equivalent of <see cref="MemoryMarshal.Cast{TFrom,TTo}(System.Span{TFrom})"/>,
/// so we wrap the source memory and cast per span/pin. The source (an mmap-backed section or an array)
/// owns the bytes; this only re-types the view.</summary>
internal sealed unsafe class CastMemoryManager<T> : MemoryManager<T> where T : struct
{
    private readonly ReadOnlyMemory<byte> _bytes;

    public CastMemoryManager(ReadOnlyMemory<byte> bytes) => _bytes = bytes;

    public override Span<T> GetSpan() =>
        MemoryMarshal.Cast<byte, T>(MemoryMarshal.AsMemory(_bytes).Span);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        MemoryHandle h = _bytes.Pin();
        return new MemoryHandle((byte*)h.Pointer + elementIndex * Unsafe.SizeOf<T>(), default, this);
    }

    public override void Unpin() { }
    protected override void Dispose(bool disposing) { } // the source memory owns the bytes
}

