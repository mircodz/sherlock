using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace Sherlock.Core.Storage;

/// <summary>
/// A working array in a temporary file mapped for reading and writing, so a heap-sized array pages to disk instead of
/// growing the process. It starts zeroed. The file is unlinked at once on Unix, so nothing is left behind even if the
/// process dies, and deleted on close on Windows.
/// </summary>
internal sealed unsafe class ScratchArray<T> : IDisposable where T : unmanaged
{
    private readonly FileStream _file;
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private bool _disposed;

    public ScratchArray(string directory, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
        long bytes = Math.Max(checked(length * sizeof(T)), 1); // a mapping can't be empty
        string path = Path.Combine(directory, $".scratch-{Guid.NewGuid():N}");
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1,
            OperatingSystem.IsWindows() ? FileOptions.DeleteOnClose : FileOptions.None);
        MemoryMappedFile? map = null;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.Delete(path);
            }
            _file.SetLength(bytes);
            map = MemoryMappedFile.CreateFromFile(_file, null, bytes, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
            _view = map.CreateViewAccessor(0, bytes, MemoryMappedFileAccess.ReadWrite);
            _map = map;
        }
        catch
        {
            map?.Dispose();
            _file.Dispose();
            throw;
        }
        byte* pointer = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        Pointer = (T*)pointer;
    }

    public long Length { get; }

    /// <summary>The first element, valid until disposal.</summary>
    public T* Pointer { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _map.Dispose();
        _file.Dispose();
    }
}
