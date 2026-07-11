using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace Sherlock.Core.Storage;

/// <summary>
/// A read-only memory-mapped view of a file as <see cref="ReadOnlyMemory{Byte}"/>: zero-copy, paged
/// in on demand, no whole-file allocation. Disposing unmaps. Files &gt; 2 GiB aren't supported yet
/// (a single view is <c>int</c>-bounded — chunked views would be the fix).
/// </summary>
internal sealed unsafe class MemoryMappedRegion : MemoryManager<byte>
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _pointer;
    private readonly int _length;

    private MemoryMappedRegion(MemoryMappedFile file, MemoryMappedViewAccessor view, byte* pointer, int length)
    {
        _file = file;
        _view = view;
        _pointer = pointer;
        _length = length;
    }

    public static MemoryMappedRegion Open(string path)
    {
        long size = new FileInfo(path).Length;
        if (size == 0)
        {
            throw new InvalidDataException($"container '{path}' is empty");
        }
        if (size > int.MaxValue)
        {
            throw new NotSupportedException($"container '{path}' exceeds 2 GiB; chunked views are not yet supported");
        }

        MemoryMappedFile file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        MemoryMappedViewAccessor view = file.CreateViewAccessor(0, size, MemoryMappedFileAccess.Read);
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        pointer += view.PointerOffset; // the view may start at an allocation-granularity boundary
        return new MemoryMappedRegion(file, view, pointer, (int)size);
    }

    public override Span<byte> GetSpan() => new(_pointer, _length);

    public override MemoryHandle Pin(int elementIndex = 0) => new(_pointer + elementIndex);

    public override void Unpin() { }

    protected override void Dispose(bool disposing)
    {
        if (_pointer != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }
        _view.Dispose();
        _file.Dispose();
    }
}
