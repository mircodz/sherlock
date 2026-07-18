using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace Sherlock.Core.Storage;

/// <summary>
/// A read-only view of a file mapped in fixed-size chunks, so files (and individual reads) larger than
/// 2&nbsp;GiB work — the <see cref="Memory{T}"/>/<see cref="Span{T}"/> and single-view APIs are all
/// <c>int</c>-length-bounded, which caps a single mapping at ~2&nbsp;GiB. Here the file is mapped as N
/// views of at most <see cref="ChunkSize"/> bytes each (allocation-granularity aligned), and a global
/// <c>long</c> offset resolves to <c>(chunk, offsetInChunk)</c>. Callers read via <see cref="CopyTo"/>,
/// which stitches a range that straddles a chunk boundary.
/// </summary>
public sealed unsafe class ChunkedMmap : IDisposable
{
    /// <summary>Bytes per mapped view. 1&nbsp;GiB — safely under <see cref="int.MaxValue"/> and a
    /// multiple of every platform's mmap allocation granularity (64&nbsp;KiB), so chunk starts are
    /// always view-aligned.</summary>
    public const long ChunkSize = 1L << 30;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor[] _views;
    private readonly byte*[] _pointers;
    private readonly long[] _lengths; // usable bytes in each chunk (last one is short)
    private bool _disposed;

    public long Length { get; }

    private ChunkedMmap(MemoryMappedFile file, MemoryMappedViewAccessor[] views, byte*[] pointers, long[] lengths, long length)
    {
        _file = file;
        _views = views;
        _pointers = pointers;
        _lengths = lengths;
        Length = length;
    }

    public static ChunkedMmap Open(string path)
    {
        long size = new FileInfo(path).Length;
        if (size == 0)
        {
            throw new InvalidDataException($"file '{path}' is empty");
        }

        var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        int chunkCount = (int)((size + ChunkSize - 1) / ChunkSize);
        var views = new MemoryMappedViewAccessor[chunkCount];
        var pointers = new byte*[chunkCount];
        var lengths = new long[chunkCount];
        try
        {
            for (int i = 0; i < chunkCount; i++)
            {
                long offset = (long)i * ChunkSize;
                long len = Math.Min(ChunkSize, size - offset);
                MemoryMappedViewAccessor view = file.CreateViewAccessor(offset, len, MemoryMappedFileAccess.Read);
                byte* p = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                p += view.PointerOffset; // the view may begin at a granularity boundary before `offset`
                views[i] = view;
                pointers[i] = p;
                lengths[i] = len;
            }
        }
        catch
        {
            Cleanup(views, pointers);
            file.Dispose();
            throw;
        }
        return new ChunkedMmap(file, views, pointers, lengths, size);
    }

    /// <summary>Copies <paramref name="dest"/>.Length bytes starting at global byte <paramref name="offset"/>
    /// into <paramref name="dest"/>, spanning chunk boundaries as needed.</summary>
    public void CopyTo(long offset, Span<byte> dest)
    {
        if (offset < 0 || offset + dest.Length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "read exceeds file bounds");
        }

        int written = 0;
        while (written < dest.Length)
        {
            long global = offset + written;
            int chunk = (int)(global / ChunkSize);
            int inChunk = (int)(global % ChunkSize);
            int available = (int)Math.Min(_lengths[chunk] - inChunk, dest.Length - written);
            var src = new ReadOnlySpan<byte>(_pointers[chunk] + inChunk, available);
            src.CopyTo(dest[written..]);
            written += available;
        }
    }

    /// <summary>If the range <c>[offset, offset+length)</c> lies within a single chunk, returns a
    /// zero-copy span over it; otherwise returns false (the caller must <see cref="CopyTo"/>).</summary>
    public bool TryGetContiguous(long offset, int length, out ReadOnlySpan<byte> span)
    {
        if (TryGetPointer(offset, length, out nint ptr))
        {
            span = new ReadOnlySpan<byte>((byte*)ptr, length);
            return true;
        }
        span = default;
        return false;
    }

    /// <summary>If the range <c>[offset, offset+length)</c> lies within a single chunk, returns a raw
    /// pointer to its start; otherwise false. The pointer is valid until this map is disposed.</summary>
    public bool TryGetPointer(long offset, int length, out nint pointer)
    {
        if (offset >= 0 && length >= 0 && offset + length <= Length)
        {
            int chunk = (int)(offset / ChunkSize);
            int inChunk = (int)(offset % ChunkSize);
            if (inChunk + length <= _lengths[chunk])
            {
                pointer = (nint)(_pointers[chunk] + inChunk);
                return true;
            }
        }
        pointer = 0;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup(_views, _pointers);
        _file.Dispose();
    }

    private static void Cleanup(MemoryMappedViewAccessor[] views, byte*[] pointers)
    {
        for (int i = 0; i < views.Length; i++)
        {
            if (views[i] is null) continue;
            if (pointers[i] != null) views[i].SafeMemoryMappedViewHandle.ReleasePointer();
            views[i].Dispose();
        }
    }
}
