using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace Sherlock.Core.Storage;

/// <summary>A read-only mapping of a whole file. Pointers and spans into it are valid until disposal.</summary>
public sealed unsafe class MappedFile : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _pointer;
    private bool _disposed;

    public long Length { get; }

    private MappedFile(MemoryMappedFile file, MemoryMappedViewAccessor view, byte* pointer, long length)
    {
        _file = file;
        _view = view;
        _pointer = pointer;
        Length = length;
    }

    public static MappedFile Open(string path)
    {
        long length = new FileInfo(path).Length;
        if (length == 0)
        {
            throw new InvalidDataException($"file '{path}' is empty");
        }

        var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        MemoryMappedViewAccessor? view = null;
        try
        {
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            return new MappedFile(file, view, pointer, length);
        }
        catch
        {
            view?.Dispose();
            file.Dispose();
            throw;
        }
    }

    /// <summary>The first byte of the file.</summary>
    internal byte* Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _pointer;
        }
    }

    /// <summary>The bytes at <paramref name="offset"/>, without copying.</summary>
    public ReadOnlySpan<byte> Span(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset > Length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "read exceeds file bounds");
        }
        return new ReadOnlySpan<byte>(Pointer + offset, length);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
