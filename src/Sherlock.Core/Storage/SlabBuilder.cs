using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;

namespace Sherlock.Core.Storage;

/// <summary>
/// A slab written in place: sections are placed in file order and filled with positional writes from any thread, or
/// through a mapping, then the file is synced and published by rename. Until then it's a temporary file beside it.
/// </summary>
internal sealed unsafe class SlabBuilder : IDisposable
{
    private readonly List<(SectionType Type, uint RecordSize, long Offset, long Length)> _sections = [];
    private readonly FileStream _stream;
    private readonly string _path;
    private readonly string _temp;
    private readonly int _sectionCount;
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private bool _committed;

    public SlabBuilder(string path, int sectionCount)
    {
        _path = path;
        _sectionCount = sectionCount;
        _temp = $"{path}.{Guid.NewGuid():N}.tmp";
        _stream = new FileStream(_temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1);
        Next = Align(ContainerFormat.HeaderSize + (long)sectionCount * ContainerFormat.SectionEntrySize);
    }

    public SafeFileHandle Handle => _stream.SafeFileHandle;

    /// <summary>Where the next placed section starts.</summary>
    public long Next { get; private set; }

    public long Place(SectionType type, int recordSize, long count)
    {
        if (_map is not null)
        {
            throw new InvalidOperationException("Sections can't be placed once the slab is mapped.");
        }
        long offset = Next;
        long length = checked(recordSize * count);
        _sections.Add((type, (uint)recordSize, offset, length));
        Next = Align(offset + length);
        return offset;
    }

    public void Write(ReadOnlySpan<byte> bytes, long offset) => RandomAccess.Write(Handle, bytes, offset);

    /// <summary>Maps the placed sections for reading and writing. Returns the file's first byte, valid until commit.</summary>
    public byte* Map()
    {
        if (_map is not null)
        {
            throw new InvalidOperationException("The slab is already mapped.");
        }
        long length = Math.Max(End, 1);
        _stream.SetLength(length);
        _map = MemoryMappedFile.CreateFromFile(_stream, null, length, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
        _view = _map.CreateViewAccessor(0, length, MemoryMappedFileAccess.ReadWrite);
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        return pointer;
    }

    public void Commit(SlabKind kind, uint version)
    {
        if (_sections.Count != _sectionCount)
        {
            throw new InvalidOperationException($"The slab placed {_sections.Count} sections, not {_sectionCount}.");
        }
        if (_view is not null)
        {
            _view.Flush();
        }
        Unmap();
        var header = new byte[ContainerFormat.HeaderSize + _sectionCount * ContainerFormat.SectionEntrySize];
        ContainerFormat.Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), ContainerFormat.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)kind);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), version);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)_sectionCount);
        for (int i = 0; i < _sectionCount; i++)
        {
            Span<byte> entry = header.AsSpan(ContainerFormat.HeaderSize + i * ContainerFormat.SectionEntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)_sections[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], _sections[i].RecordSize);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], (ulong)_sections[i].Offset);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], (ulong)_sections[i].Length);
        }
        Write(header, 0);
        _stream.SetLength(End);
        _stream.Flush(flushToDisk: true);
        _stream.Dispose();
        File.Move(_temp, _path, overwrite: true);
        _committed = true;
    }

    public void Dispose()
    {
        Unmap();
        _stream.Dispose();
        if (!_committed)
        {
            try { File.Delete(_temp); } catch (IOException) { } // best effort
        }
    }

    private long End => _sections.Count == 0 ? Next : _sections[^1].Offset + _sections[^1].Length;

    private void Unmap()
    {
        if (_view is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _view = null;
        }
        _map?.Dispose();
        _map = null;
    }

    private static long Align(long n) => (n + ContainerFormat.Alignment - 1) & ~((long)ContainerFormat.Alignment - 1);
}
