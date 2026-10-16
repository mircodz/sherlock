using System;
using System.Buffers;

namespace Sherlock.Core.Storage;

/// <summary>
/// A long-indexed column of fixed-width records in a mapped file. The file must stay open while the column, or
/// anything read from it without copying, is used.
/// </summary>
public sealed unsafe class Column<T> where T : unmanaged
{
    private readonly MappedFile? _file;
    private readonly long _offset;

    /// <summary>The number of records.</summary>
    public long Length { get; }

    /// <summary>An empty column.</summary>
    public static Column<T> Empty { get; } = new(null, 0, 0);

    internal Column(MappedFile? file, long byteOffset, long length)
    {
        _file = file;
        _offset = byteOffset;
        Length = length;
    }

    private T* Items => (T*)(_file!.Pointer + _offset);

    public T this[long i] => (ulong)i < (ulong)Length ? Items[i] : throw new ArgumentOutOfRangeException(nameof(i));

    /// <summary><paramref name="length"/> records at <paramref name="start"/>, without copying.</summary>
    public ReadOnlySpan<T> Slice(long start, int length)
    {
        CheckRange(start, length);
        return length == 0 ? default : new ReadOnlySpan<T>(Items + start, length);
    }

    public void CopyTo(long start, Span<T> dest) => Slice(start, dest.Length).CopyTo(dest);

    /// <summary>The whole column as memory backed by the mapping.</summary>
    public ReadOnlyMemory<T> AsMemory() => Length <= int.MaxValue
        ? AsMemory(0, (int)Length)
        : throw new InvalidOperationException($"The column holds {Length:N0} records, more than one span can address.");

    /// <summary><paramref name="length"/> records at <paramref name="start"/> as memory backed by the mapping.</summary>
    public ReadOnlyMemory<T> AsMemory(long start, int length)
    {
        CheckRange(start, length);
        return length == 0 ? ReadOnlyMemory<T>.Empty : new PointerMemory<T>(Items + start, length).Memory;
    }

    private void CheckRange(long start, int length)
    {
        if (start < 0 || length < 0 || start > Length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"[{start}, {start + length}) is outside a column of {Length} records");
        }
    }
}

/// <summary>Exposes a mapped pointer as a <see cref="ReadOnlyMemory{T}"/> without copying. The owning
/// <see cref="MappedFile"/> keeps the pointer valid; this manager frees nothing.</summary>
internal sealed unsafe class PointerMemory<T>(T* pointer, int length) : MemoryManager<T> where T : unmanaged
{
    public override Span<T> GetSpan() => new(pointer, length);
    public override MemoryHandle Pin(int elementIndex = 0) => new(pointer + elementIndex);
    public override void Unpin() { }
    protected override void Dispose(bool disposing) { } // the mapping owns the memory, not this manager
}
