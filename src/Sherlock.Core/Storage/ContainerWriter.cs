using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sherlock.Core.Storage;

/// <summary>Builds a storage container, byte-for-byte identical to the native writer (guarded by the golden-bytes test). Used for tests/tooling.</summary>
public sealed class ContainerWriter
{
    // A section's payload is a list of byte-chunks written back-to-back. Most sections have exactly one
    // chunk; a huge column (the heap-graph edges) is added as many ≤~1 GB chunks so the whole file — and
    // any single section — can exceed 2 GB without ever materializing as one array.
    private readonly List<Sec> _sections = [];

    private readonly record struct Sec(SectionType Type, ushort Version, ushort RecordSize, ulong Count, IReadOnlyList<ReadOnlyMemory<byte>> Chunks, long Length);

    /// <summary>Adds a section of raw bytes (<paramref name="recordSize"/> = 0 for blob sections).</summary>
    public void AddSection(SectionType type, ushort version, ushort recordSize, ReadOnlySpan<byte> data, ulong count)
        => _sections.Add(new Sec(type, version, recordSize, count, [data.ToArray()], data.Length));

    /// <summary>Adds a fixed-width record section from a span of unmanaged <typeparamref name="T"/>.</summary>
    public void AddRecords<T>(SectionType type, ushort version, ReadOnlySpan<T> records) where T : struct
        => AddSection(type, version, (ushort)Unsafe.SizeOf<T>(), MemoryMarshal.AsBytes(records), (ulong)records.Length);

    /// <summary>Adds a section whose payload is supplied as several byte-chunks written contiguously —
    /// so a section larger than <see cref="int.MaxValue"/> bytes can be emitted without concatenating
    /// into one array. <paramref name="count"/> is the logical record count across all chunks.</summary>
    public void AddChunkedSection(SectionType type, ushort version, ushort recordSize, IReadOnlyList<ReadOnlyMemory<byte>> chunks, ulong count)
    {
        long length = 0;
        foreach (ReadOnlyMemory<byte> c in chunks) length += c.Length;
        _sections.Add(new Sec(type, version, recordSize, count, chunks, length));
    }

    // Computes each section's aligned byte offset in the file; returns the offsets and the total size.
    private long[] Layout(out long total)
    {
        long tableEnd = ContainerFormat.HeaderSize + (long)_sections.Count * ContainerFormat.SectionEntrySize;
        var offsets = new long[_sections.Count];
        long cursor = tableEnd;
        for (int i = 0; i < _sections.Count; i++)
        {
            cursor = Align(cursor);
            offsets[i] = cursor;
            cursor += _sections[i].Length;
        }
        total = _sections.Count == 0 ? tableEnd : cursor;
        return offsets;
    }

    private void WriteHeaderAndTable(Stream stream, long[] offsets)
    {
        Span<byte> head = stackalloc byte[ContainerFormat.HeaderSize];
        ContainerFormat.Magic.CopyTo(head);
        BinaryPrimitives.WriteUInt16LittleEndian(head[4..], ContainerFormat.FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(head[6..], ContainerFormat.FlagLittleEndian);
        BinaryPrimitives.WriteUInt32LittleEndian(head[8..], (uint)_sections.Count);
        // reserved [12..16) stays zero
        stream.Write(head);

        Span<byte> e = stackalloc byte[ContainerFormat.SectionEntrySize];
        for (int i = 0; i < _sections.Count; i++)
        {
            Sec s = _sections[i];
            e.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)s.Type);
            BinaryPrimitives.WriteUInt16LittleEndian(e[4..], s.Version);
            BinaryPrimitives.WriteUInt16LittleEndian(e[6..], s.RecordSize);
            BinaryPrimitives.WriteUInt64LittleEndian(e[8..], (ulong)offsets[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(e[16..], (ulong)s.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(e[24..], s.Count);
            stream.Write(e);
        }
    }

    /// <summary>Streams the whole container to <paramref name="stream"/> — header, section table, then
    /// each section's bytes at its aligned offset. Never holds more than one section-chunk in memory, so
    /// it works for multi-gigabyte containers.</summary>
    public void WriteTo(Stream stream)
    {
        long[] offsets = Layout(out _);
        WriteHeaderAndTable(stream, offsets);

        long written = ContainerFormat.HeaderSize + (long)_sections.Count * ContainerFormat.SectionEntrySize;
        Span<byte> pad = stackalloc byte[ContainerFormat.Alignment];
        pad.Clear();
        for (int i = 0; i < _sections.Count; i++)
        {
            long gap = offsets[i] - written;
            if (gap > 0) { stream.Write(pad[..(int)gap]); written += gap; }
            foreach (ReadOnlyMemory<byte> chunk in _sections[i].Chunks)
            {
                stream.Write(chunk.Span);
                written += chunk.Length;
            }
        }
    }

    /// <summary>Writes the container to a file, streaming (so it supports files &gt; 2&nbsp;GB).</summary>
    public void Save(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        WriteTo(fs);
    }

    /// <summary>Materializes the whole container as one array. Only valid for containers under
    /// <see cref="int.MaxValue"/> bytes (small in-memory containers, tests); large ones must use
    /// <see cref="Save"/>/<see cref="WriteTo"/>.</summary>
    public byte[] ToArray()
    {
        Layout(out long total);
        if (total > int.MaxValue)
        {
            throw new InvalidOperationException($"container is {total} bytes (> 2 GB); use Save/WriteTo instead of ToArray.");
        }
        var buf = new byte[total];
        using var ms = new MemoryStream(buf, writable: true);
        WriteTo(ms);
        return buf;
    }

    private static long Align(long n) => (n + ContainerFormat.Alignment - 1) & ~((long)ContainerFormat.Alignment - 1);
}
