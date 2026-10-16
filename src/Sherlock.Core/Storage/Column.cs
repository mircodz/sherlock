using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Sherlock.Core.Storage;

/// <summary>
/// A long-indexed column of fixed-width records across one or more sections of a mapped file.
/// The file must stay open while the column, or anything read from it without copying, is used.
/// </summary>
public sealed unsafe class Column<T> where T : unmanaged
{
    // A physical section; FirstElement is its running (global) start index.
    private readonly struct Seg(long byteOffset, long firstElement, long count)
    {
        public readonly long ByteOffset = byteOffset;
        public readonly long FirstElement = firstElement;
        public readonly long Count = count;
    }

    private readonly MappedFile _file;
    private readonly Seg[] _segs;
    private static readonly int Width = Unsafe.SizeOf<T>();

    /// <summary>Total element count across every section.</summary>
    public long Length { get; }

    /// <summary>An empty column (no sections).</summary>
    public static Column<T> Empty { get; } = new();

    private Column()
    {
        _file = null!;
        _segs = [];
    }

    /// <summary>Builds a column from ordered sections, each described by byte offset and element count.</summary>
    public Column(MappedFile file, ReadOnlySpan<(long byteOffset, long count)> sections)
    {
        _file = file;
        _segs = new Seg[sections.Length];
        long running = 0;
        for (int i = 0; i < sections.Length; i++)
        {
            _segs[i] = new Seg(sections[i].byteOffset, running, sections[i].count);
            running += sections[i].count;
        }
        Length = running;
    }

    public T this[long i]
    {
        get
        {
            if ((ulong)i >= (ulong)Length)
            {
                throw new ArgumentOutOfRangeException(nameof(i));
            }
            Seg seg = SegmentOf(i);
            return Unsafe.ReadUnaligned<T>(_file.Pointer + seg.ByteOffset + (i - seg.FirstElement) * Width);
        }
    }

    /// <summary>A zero-copy span of <paramref name="length"/> elements at <paramref name="start"/>, when the run fits
    /// one section. Full scans should use <see cref="CopyTo"/>; the whole column may exceed <see cref="int.MaxValue"/>
    /// elements.</summary>
    public ReadOnlySpan<T> Slice(long start, int length)
    {
        if (!TrySlice(start, length, out ReadOnlySpan<T> span))
        {
            throw new InvalidOperationException($"slice [{start}, {start + length}) spans a section boundary; use CopyTo");
        }
        return span;
    }

    /// <summary>Zero-copy span if the run fits one section; false if it crosses into the next.</summary>
    public bool TrySlice(long start, int length, out ReadOnlySpan<T> span)
    {
        span = default;
        if (length == 0)
        {
            return true;
        }
        if (start < 0 || length < 0 || start + length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }
        Seg seg = SegmentOf(start);
        if (start + length > seg.FirstElement + seg.Count)
        {
            return false;
        }
        span = new ReadOnlySpan<T>(_file.Pointer + seg.ByteOffset + (start - seg.FirstElement) * Width, length);
        return true;
    }

    /// <summary>Copies <paramref name="dest"/>.Length elements starting at <paramref name="start"/>, across section
    /// boundaries.</summary>
    public void CopyTo(long start, Span<T> dest)
    {
        if (start < 0 || start + dest.Length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }
        long i = start;
        int written = 0;
        while (written < dest.Length)
        {
            Seg seg = SegmentOf(i);
            long inSeg = i - seg.FirstElement;
            int take = (int)Math.Min(seg.Count - inSeg, dest.Length - written);
            new ReadOnlySpan<T>(_file.Pointer + seg.ByteOffset + inSeg * Width, take).CopyTo(dest.Slice(written, take));
            written += take;
            i += take;
        }
    }

    /// <summary>The whole column as memory backed by the mapping. Sections written back to back read as one run;
    /// only sections with gaps between them are copied.</summary>
    public ReadOnlyMemory<T> AsMemory()
    {
        if (Length == 0)
        {
            return ReadOnlyMemory<T>.Empty;
        }
        if (Length > int.MaxValue)
        {
            throw new InvalidOperationException("column exceeds the managed array limit");
        }
        for (int i = 1; i < _segs.Length; i++)
        {
            if (_segs[i].ByteOffset != _segs[i - 1].ByteOffset + _segs[i - 1].Count * Width)
            {
                var copy = GC.AllocateUninitializedArray<T>((int)Length);
                CopyTo(0, copy);
                return copy;
            }
        }
        return new PointerMemory<T>((T*)(_file.Pointer + _segs[0].ByteOffset), (int)Length).Memory;
    }

    private Seg SegmentOf(long i)
    {
        int lo = 0, hi = _segs.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_segs[mid].FirstElement <= i)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return _segs[lo];
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
