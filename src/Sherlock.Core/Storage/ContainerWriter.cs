using System;
using System.Buffers;
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
    // chunk; a huge column (the heap-graph edges) is added as many ≤~1 GB chunks so the file, and any
    // single section, can exceed 2 GB without ever materializing as one array.
    private readonly List<Sec> _sections = [];

    private readonly record struct Sec(SectionType Type, ushort Version, ushort RecordSize, ulong Count, IReadOnlyList<ReadOnlyMemory<byte>> Chunks, long Length);

    /// <summary>Adds a section of raw bytes (<paramref name="recordSize"/> = 0 for blob sections).</summary>
    public void AddSection(SectionType type, ushort version, ushort recordSize, ReadOnlySpan<byte> data, ulong count)
        => _sections.Add(new Sec(type, version, recordSize, count, [data.ToArray()], data.Length));

    /// <summary>Adds a fixed-width record section from a span of unmanaged <typeparamref name="T"/>.</summary>
    public void AddRecords<T>(SectionType type, ushort version, ReadOnlySpan<T> records) where T : struct
        => AddSection(type, version, (ushort)Unsafe.SizeOf<T>(), MemoryMarshal.AsBytes(records), (ulong)records.Length);

    public void AddMemoryRecords<T>(SectionType type, ushort version, ReadOnlyMemory<T> records) where T : unmanaged
    {
        ReadOnlyMemory<byte> bytes = records.Length == 0 ? ReadOnlyMemory<byte>.Empty : new UnmanagedByteMemory<T>(records).Memory;
        _sections.Add(new Sec(type, version, (ushort)Unsafe.SizeOf<T>(), (ulong)records.Length, [bytes], bytes.Length));
    }

    /// <summary>Adds a large fixed-width column as one or more same-typed sections, each at most
    /// <paramref name="chunkBytes"/> bytes and holding a uniform element count (last is short). Mirrors
    /// the native <c>addChunkedRecords</c> layout so <see cref="Column{T}"/> reassembles either side's
    /// output. A single section is capped near 2&nbsp;GB by the reader, so a per-object column (one record
    /// per live object) must be split. Any sort must already be applied; chunking is a pure partition.</summary>
    public void AddChunkedRecords<T>(SectionType type, ushort version, ReadOnlySpan<T> records,
                                     long chunkBytes = ContainerFormat.DefaultChunkBytes) where T : struct
    {
        int width = Unsafe.SizeOf<T>();
        int elemsPerChunk = (int)Math.Max(1, chunkBytes / width);
        for (int i = 0; i < records.Length; i += elemsPerChunk)
        {
            int n = Math.Min(elemsPerChunk, records.Length - i);
            AddRecords(type, version, records.Slice(i, n));
        }
    }

    public void AddChunkedMemoryRecords<T>(SectionType type, ushort version, ReadOnlyMemory<T> records,
                                           long chunkBytes = ContainerFormat.DefaultChunkBytes) where T : unmanaged
    {
        int width = Unsafe.SizeOf<T>();
        int elemsPerChunk = (int)Math.Max(1, chunkBytes / width);
        for (int i = 0; i < records.Length; i += elemsPerChunk)
        {
            int count = Math.Min(elemsPerChunk, records.Length - i);
            AddMemoryRecords(type, version, records.Slice(i, count));
        }
    }

    /// <summary>Adds a section whose payload is supplied as several byte-chunks written contiguously, so
    /// a section larger than <see cref="int.MaxValue"/> bytes can be emitted without concatenating into
    /// one array. <paramref name="count"/> is the logical record count across all chunks.</summary>
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

    /// <summary>Streams the whole container to <paramref name="stream"/>: header, section table, then
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
        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                WriteTo(file);
                file.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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

internal sealed unsafe class UnmanagedByteMemory<T>(ReadOnlyMemory<T> source) : MemoryManager<byte> where T : unmanaged
{
    private readonly ReadOnlyMemory<T> _source = source;

    public override Span<byte> GetSpan() => MemoryMarshal.AsBytes(MemoryMarshal.AsMemory(_source).Span);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if ((uint)elementIndex > (uint)GetSpan().Length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }
        MemoryHandle sourceHandle = _source.Pin();
        var pin = new SourcePin(sourceHandle);
        return new MemoryHandle((byte*)sourceHandle.Pointer + elementIndex, default, pin);
    }

    public override void Unpin() { }
    protected override void Dispose(bool disposing) { }

    private sealed class SourcePin(MemoryHandle handle) : IPinnable
    {
        private MemoryHandle _handle = handle;
        public MemoryHandle Pin(int elementIndex) => throw new NotSupportedException();
        public void Unpin() => _handle.Dispose();
    }
}
