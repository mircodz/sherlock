using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sherlock.Core.Storage;

/// <summary>Builds a slab in the layout the native writer uses. Sections added from memory are borrowed, not
/// copied, until the slab is written.</summary>
public sealed class ContainerWriter(SlabKind kind, uint version)
{
    private readonly record struct Section(SectionType Type, uint RecordSize, IReadOnlyList<ReadOnlyMemory<byte>> Parts, long Length);

    private readonly List<Section> _sections = [];

    /// <summary>A section of raw bytes, copied.</summary>
    public void AddBytes(SectionType type, ReadOnlySpan<byte> bytes) => Add(type, 1, [bytes.ToArray()]);

    /// <summary>A section of fixed-width records, copied; for small sections.</summary>
    public void AddRecords<T>(SectionType type, ReadOnlySpan<T> records) where T : unmanaged =>
        Add(type, (uint)Unsafe.SizeOf<T>(), [MemoryMarshal.AsBytes(records).ToArray()]);

    /// <summary>A section of fixed-width records, borrowed until the slab is written.</summary>
    public void AddMemory<T>(SectionType type, ReadOnlyMemory<T> records) where T : unmanaged => AddMemory(type, [records]);

    /// <summary>One section written from several runs of records in order, each borrowed until the slab is written.</summary>
    public void AddMemory<T>(SectionType type, IReadOnlyList<ReadOnlyMemory<T>> parts) where T : unmanaged
    {
        var bytes = new ReadOnlyMemory<byte>[parts.Count];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = parts[i].IsEmpty ? ReadOnlyMemory<byte>.Empty : new UnmanagedByteMemory<T>(parts[i]).Memory;
        }
        Add(type, (uint)Unsafe.SizeOf<T>(), bytes);
    }

    private void Add(SectionType type, uint recordSize, IReadOnlyList<ReadOnlyMemory<byte>> parts)
    {
        if (_sections.Exists(section => section.Type == type))
        {
            throw new InvalidOperationException($"Section {type} was already added.");
        }
        long length = 0;
        foreach (ReadOnlyMemory<byte> part in parts)
        {
            length += part.Length;
        }
        _sections.Add(new Section(type, recordSize, parts, length));
    }

    private long[] Layout(out long total)
    {
        var offsets = new long[_sections.Count];
        long cursor = Align(ContainerFormat.HeaderSize + (long)_sections.Count * ContainerFormat.SectionEntrySize);
        for (int i = 0; i < _sections.Count; i++)
        {
            offsets[i] = cursor;
            cursor = Align(cursor + _sections[i].Length);
        }
        total = _sections.Count == 0 ? ContainerFormat.HeaderSize : offsets[^1] + _sections[^1].Length;
        return offsets;
    }

    /// <summary>Writes the header, the section table, and each section, without materializing the whole slab.</summary>
    public void WriteTo(Stream stream)
    {
        long[] offsets = Layout(out _);
        Span<byte> head = stackalloc byte[ContainerFormat.HeaderSize];
        ContainerFormat.Magic.CopyTo(head);
        BinaryPrimitives.WriteUInt16LittleEndian(head[4..], ContainerFormat.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(head[6..], (ushort)kind);
        BinaryPrimitives.WriteUInt32LittleEndian(head[8..], version);
        BinaryPrimitives.WriteUInt32LittleEndian(head[12..], (uint)_sections.Count);
        stream.Write(head);

        Span<byte> entry = stackalloc byte[ContainerFormat.SectionEntrySize];
        for (int i = 0; i < _sections.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)_sections[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], _sections[i].RecordSize);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], (ulong)offsets[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], (ulong)_sections[i].Length);
            stream.Write(entry);
        }

        long written = ContainerFormat.HeaderSize + (long)_sections.Count * ContainerFormat.SectionEntrySize;
        Span<byte> zeros = stackalloc byte[256];
        zeros.Clear();
        for (int i = 0; i < _sections.Count; i++)
        {
            for (long gap = offsets[i] - written; gap > 0;)
            {
                int n = (int)Math.Min(gap, zeros.Length);
                stream.Write(zeros[..n]);
                gap -= n;
            }
            foreach (ReadOnlyMemory<byte> part in _sections[i].Parts)
            {
                stream.Write(part.Span);
            }
            written = offsets[i] + _sections[i].Length;
        }
    }

    /// <summary>Streams and flushes a new slab before atomically replacing the destination.</summary>
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

    /// <summary>The whole slab as one array; for slabs under 2 GiB, such as in tests.</summary>
    public byte[] ToArray()
    {
        Layout(out long total);
        var buffer = new byte[checked((int)total)];
        using var stream = new MemoryStream(buffer, writable: true);
        WriteTo(stream);
        return buffer;
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
