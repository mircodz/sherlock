using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sherlock.Core.Storage;

/// <summary>
/// A long-indexed, memory-mapped column of fixed-width <typeparamref name="T"/> records: one on-disk
/// section or many (a large column is written as several same-typed sections). Callers index by
/// <c>long</c> and never see mmap views, chunk boundaries, or the ~2&nbsp;GB single-section ceiling;
/// access is zero-copy.
/// </summary>
public sealed unsafe class Column<T> where T : unmanaged
{
    // A physical section; FirstElement is its running (global) start index.
    private readonly struct Seg
    {
        public readonly long ByteOffset;
        public readonly long FirstElement;
        public readonly long Count;
        public Seg(long byteOffset, long firstElement, long count)
        {
            ByteOffset = byteOffset;
            FirstElement = firstElement;
            Count = count;
        }
    }

    private readonly ChunkedMmap _mmap;
    private readonly Seg[] _segs;
    private static readonly int Width = Unsafe.SizeOf<T>();

    /// <summary>Total element count across every section.</summary>
    public long Length { get; }

    /// <summary>An empty column (no sections).</summary>
    public static Column<T> Empty { get; } = new();

    private Column()
    {
        _mmap = null!;
        _segs = [];
        Length = 0;
    }

    /// <summary>Builds a column from its physical sections in global order (byte offset + element count
    /// each). Any partition works; indexing relies only on the running totals.</summary>
    public Column(ChunkedMmap mmap, ReadOnlySpan<(long byteOffset, long count)> sections)
    {
        _mmap = mmap;
        _segs = new Seg[sections.Length];
        long running = 0;
        for (int i = 0; i < sections.Length; i++)
        {
            _segs[i] = new Seg(sections[i].byteOffset, running, sections[i].count);
            running += sections[i].count;
        }
        Length = running;
    }

    /// <summary>Random access to element <paramref name="i"/> (binary search / point lookups).</summary>
    public T this[long i]
    {
        get
        {
            if ((ulong)i >= (ulong)Length)
            {
                throw new ArgumentOutOfRangeException(nameof(i));
            }
            Seg seg = SegmentOf(i);
            long byteOffset = seg.ByteOffset + (i - seg.FirstElement) * Width;
            if (_mmap.TryGetPointer(byteOffset, Width, out nint p))
            {
                return Unsafe.ReadUnaligned<T>((void*)p);
            }
            // A record can straddle a 1 GiB mmap-view boundary when Width doesn't divide ChunkSize
            // (non-power-of-two records); stitch a copy in that case.
            T value;
            _mmap.CopyTo(byteOffset, new Span<byte>(&value, Width));
            return value;
        }
    }

    /// <summary>A zero-copy span of <paramref name="length"/> elements at <paramref name="start"/>, when
    /// the run fits one section and one mmap view. Full scans should use <see cref="CopyTo"/>; the whole
    /// column may exceed <see cref="int.MaxValue"/> elements.</summary>
    public ReadOnlySpan<T> Slice(long start, int length)
    {
        if (!TrySlice(start, length, out ReadOnlySpan<T> span))
        {
            throw new InvalidOperationException($"slice [{start}, {start + length}) spans a section/chunk boundary; use CopyTo");
        }
        return span;
    }

    /// <summary>Zero-copy span if the run fits one section and mmap view; false if it straddles a boundary.</summary>
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
            return false; // crosses a section boundary
        }
        long byteOffset = seg.ByteOffset + (start - seg.FirstElement) * Width;
        long byteLength = (long)length * Width;
        if (byteLength <= int.MaxValue && _mmap.TryGetContiguous(byteOffset, (int)byteLength, out ReadOnlySpan<byte> bytes))
        {
            span = MemoryMarshal.Cast<byte, T>(bytes);
            return true;
        }
        return false; // crosses an mmap chunk boundary (or too large for one span)
    }

    /// <summary>Copies <paramref name="dest"/>.Length elements starting at <paramref name="start"/>,
    /// stitching across section and mmap-chunk boundaries. The boundary-safe way to scan a column.</summary>
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
            long byteOffset = seg.ByteOffset + inSeg * Width;
            Span<byte> destBytes = MemoryMarshal.AsBytes(dest.Slice(written, take));
            _mmap.CopyTo(byteOffset, destBytes);
            written += take;
            i += take;
        }
    }

    /// <summary>A zero-copy <see cref="ReadOnlyMemory{T}"/> over the whole column, only when it's a single
    /// section within one mmap chunk (e.g. one edge chunk); throws otherwise. Valid until the owning
    /// <see cref="SlabFile"/> is disposed.</summary>
    public ReadOnlyMemory<T> AsMemory()
    {
        if (Length == 0)
        {
            return ReadOnlyMemory<T>.Empty;
        }
        if (_segs.Length == 1 && Length <= int.MaxValue &&
            _mmap.TryGetPointer(_segs[0].ByteOffset, (int)(Length * Width), out nint ptr))
        {
            return new PointerMemory<T>((T*)ptr, (int)Length).Memory;
        }
        throw new InvalidOperationException("column is not a single contiguous mmap chunk; index or CopyTo instead");
    }

    // Binary search the (small) section list for the one owning global index i.
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

/// <summary>Exposes a raw mmap pointer as a <see cref="ReadOnlyMemory{T}"/> without copying. The owning
/// <see cref="ChunkedMmap"/> keeps the pointer valid; this manager frees nothing.</summary>
internal sealed unsafe class PointerMemory<T> : MemoryManager<T> where T : unmanaged
{
    private readonly T* _pointer;
    private readonly int _length;

    public PointerMemory(T* pointer, int length)
    {
        _pointer = pointer;
        _length = length;
    }

    public override Span<T> GetSpan() => new(_pointer, _length);
    public override MemoryHandle Pin(int elementIndex = 0) => new(_pointer + elementIndex);
    public override void Unpin() { }
    protected override void Dispose(bool disposing) { } // the mmap owns the memory, not this manager
}
