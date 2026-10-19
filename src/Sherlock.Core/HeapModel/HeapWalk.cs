using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Sherlock.Core.Storage;

namespace Sherlock.Core.HeapModel;

/// <summary>What a walk does with each object. Implemented by structs, so each walk compiles to its own loop.</summary>
internal unsafe interface IObjectSink
{
    void Object(ulong address, ulong size, HeapType type, DumpCursor cursor);
    void Free(ulong size);
}

/// <summary>
/// Walks heap segments as ClrMD does (sizes from the method table and component count, 8-byte alignment, allocation
/// contexts stepped over in small-object segments), reading headers in place from the mapped dump. Segments are
/// independent, so workers walk them in parallel.
/// </summary>
internal static unsafe class HeapWalk
{
    public const ulong MinObjectSize = 24;

    // How far ahead of a walk the dump is requested from the disk.
    private const ulong ReadAhead = 32UL << 20;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Align(ulong size) => (size + 7) & ~7UL;

    /// <summary>Walks the objects from <paramref name="from"/>, an object start, up to <paramref name="to"/> or the segment's end.</summary>
    public static void Walk<TSink>(HeapSegment segment, DumpCursor cursor, ulong from, ulong to, TypeCache types, ref TSink sink, CancellationToken cancellationToken)
        where TSink : struct, IObjectSink
    {
        ulong end = segment.End;
        ulong[] contextStarts = segment.ContextStarts, contextEnds = segment.ContextEnds;
        bool skipContexts = segment.SkipContexts;
        int next = LowerBound(contextStarts, from);
        ulong stop = Math.Min(end, to);
        ulong requested = from;
        ulong obj = from;
        while (obj != 0 && obj < stop)
        {
            if (obj + ReadAhead / 2 >= requested && requested < stop)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong first = Math.Max(requested, obj);
                requested = Math.Min(stop, first + ReadAhead);
                cursor.Memory.WillNeed(first, requested - first);
            }

            byte* p = cursor.At(obj, 16);
            ulong methodTable = (p != null ? *(ulong*)p : Read(cursor, obj, 8)) & ~7UL;
            if (methodTable == 0)
            {
                throw new InvalidDataException($"Could not read an object method table at 0x{obj:x}.");
            }
            HeapType type = types.Get(methodTable, obj);
            ulong size = type.BaseSize;
            if (type.ComponentSize != 0)
            {
                ulong count = p != null ? *(uint*)(p + 8) : Read(cursor, obj + 8, 4);
                if (type.IsString)
                {
                    count++;
                }
                size = checked(count * type.ComponentSize + type.BaseSize);
            }
            size = Math.Max(size, MinObjectSize);

            if (type.IsFree)
            {
                sink.Free(size);
            }
            else
            {
                sink.Object(obj, size, type, cursor);
            }

            obj = checked(obj + Align(size));
            if (skipContexts)
            {
                while (next < contextStarts.Length && contextStarts[next] < obj) next++;
                while (next < contextStarts.Length && contextStarts[next] == obj)
                {
                    obj = contextEnds[next++] + Align(MinObjectSize);
                    if (obj >= end)
                    {
                        obj = 0;
                        break;
                    }
                    while (next < contextStarts.Length && contextStarts[next] < obj) next++;
                }
            }
        }
    }

    // A header split by the end of a run: the method table and count read on their own, as ClrMD reads them.
    private static ulong Read(DumpCursor cursor, ulong address, int length)
    {
        byte* p = cursor.At(address, (ulong)length);
        if (p == null)
        {
            throw new InvalidDataException(length == 8
                ? $"Could not read an object method table at 0x{address:x}."
                : $"Could not read the component count at object 0x{address - 8:x}.");
        }
        return length == 8 ? *(ulong*)p : *(uint*)p;
    }

    /// <summary>Visits the references of an object as far as the dump holds it, as ClrMD does: its bytes from the start
    /// up to the first gap, which are all of them unless the dump left pages out.</summary>
    public static void References<TVisitor>(GCDescriptor descriptor, DumpCursor cursor, ulong address, ulong size, ref TVisitor visitor)
        where TVisitor : struct, IReferenceVisitor
    {
        byte* bytes = cursor.Object(address, size, out ulong captured);
        if (captured > 8)
        {
            descriptor.Walk(bytes, (long)captured, captured < size, ref visitor);
        }
    }

    /// <summary>Runs <paramref name="worker"/> on <paramref name="count"/> threads at once. The first failure stops the
    /// other workers and is rethrown as it was thrown.</summary>
    public static void Run(int count, Action<int, CancellationToken> worker, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? failure = null;
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = count }, w =>
        {
            try
            {
                worker(w, stop.Token);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                stop.Cancel();
            }
        });
        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>Objects by type name, largest total first, with free space as "Free". Memory grows with the number of
    /// types, not objects.</summary>
    public static IReadOnlyList<HeapTypeStat> Histogram(DumpHeap heap, CancellationToken cancellationToken)
    {
        HeapSegment[] largestFirst = [.. heap.Segments.OrderByDescending(s => s.Bytes)];
        var parts = new TypeHistogram[Environment.ProcessorCount];
        int next = -1;
        Run(parts.Length, (w, stop) =>
        {
            var sink = new HistogramSink(parts[w] = new TypeHistogram());
            var types = new TypeCache(heap.Types);
            using var cursor = new DumpCursor(heap.Memory);
            for (int i; (i = Interlocked.Increment(ref next)) < largestFirst.Length;)
            {
                Walk(largestFirst[i], cursor, largestFirst[i].Start, ulong.MaxValue, types, ref sink, stop);
            }
        }, cancellationToken);

        HeapType[] types = heap.Types.ToArray();
        var rows = new Dictionary<string, (long Count, ulong Bytes)>(StringComparer.Ordinal);
        foreach (TypeHistogram part in parts)
        {
            for (int t = 0; t < part.Counts.Length; t++)
            {
                if (part.Counts[t] > 0)
                {
                    ref var row = ref CollectionsMarshal.GetValueRefOrAddDefault(rows, types[t].Name, out _);
                    row = (row.Count + part.Counts[t], row.Bytes + part.Bytes[t]);
                }
            }
            if (part.FreeCount > 0)
            {
                ref var free = ref CollectionsMarshal.GetValueRefOrAddDefault(rows, "Free", out _);
                free = (free.Count + part.FreeCount, free.Bytes + part.FreeBytes);
            }
        }
        return [.. rows.Select(r => new HeapTypeStat(r.Key, r.Value.Count, r.Value.Bytes)).OrderByDescending(s => s.TotalSize)];
    }

    /// <summary>The <paramref name="limit"/> largest objects whose type name <paramref name="matches"/>, largest first, with totals
    /// over every match; previews are left to the caller. Each worker decides once per type and keeps its own top
    /// objects, so memory grows with types and the limit, not objects. Ties keep the lower address.</summary>
    public static InstanceListing Instances(DumpHeap heap, Func<string, bool> matches, int limit, CancellationToken cancellationToken)
    {
        HeapSegment[] largestFirst = [.. heap.Segments.OrderByDescending(s => s.Bytes)];
        var parts = new InstanceSearch[Environment.ProcessorCount];
        int next = -1;
        Run(parts.Length, (w, stop) =>
        {
            var sink = new InstanceSink(parts[w] = new InstanceSearch(matches, limit));
            var types = new TypeCache(heap.Types);
            using var cursor = new DumpCursor(heap.Memory);
            for (int i; (i = Interlocked.Increment(ref next)) < largestFirst.Length;)
            {
                Walk(largestFirst[i], cursor, largestFirst[i].Start, ulong.MaxValue, types, ref sink, stop);
            }
        }, cancellationToken);

        ObjectInstance[] largest = [.. parts.SelectMany(p => p.Top.UnorderedItems.Select(item => item.Element))
            .OrderByDescending(o => o.Size).ThenBy(o => o.Address).Take(limit)];
        return new InstanceListing(largest, parts.Sum(p => p.Matched), parts.Aggregate(0UL, (sum, p) => sum + p.MatchedBytes));
    }

    public static int LowerBound(ulong[] sorted, ulong value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (sorted[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    private sealed class TypeHistogram
    {
        public long[] Counts = new long[256];
        public ulong[] Bytes = new ulong[256];
        public long FreeCount;
        public ulong FreeBytes;
    }

    private readonly struct HistogramSink(TypeHistogram histogram) : IObjectSink
    {
        public void Object(ulong address, ulong size, HeapType type, DumpCursor cursor)
        {
            int t = type.Index;
            if (t >= histogram.Counts.Length)
            {
                int length = Math.Max(histogram.Counts.Length * 2, t + 1);
                Array.Resize(ref histogram.Counts, length);
                Array.Resize(ref histogram.Bytes, length);
            }
            histogram.Counts[t]++;
            histogram.Bytes[t] += size;
        }

        public void Free(ulong size)
        {
            histogram.FreeCount++;
            histogram.FreeBytes += size;
        }
    }

    /// <summary>One worker's matches: a min-heap of the largest so far, evicting the smallest (and of equal sizes, the
    /// higher address) first.</summary>
    private sealed class InstanceSearch(Func<string, bool> matches, int limit)
    {
        private sbyte[] _decided = new sbyte[256]; // per type index: 1 matches, -1 doesn't, 0 not asked yet

        public readonly PriorityQueue<ObjectInstance, (ulong Size, ulong Address)> Top = new(Comparer<(ulong Size, ulong Address)>.Create(
            (a, b) => a.Size != b.Size ? a.Size.CompareTo(b.Size) : b.Address.CompareTo(a.Address)));
        public long Matched;
        public ulong MatchedBytes;

        public void Consider(ulong address, ulong size, HeapType type)
        {
            int t = type.Index;
            if (t >= _decided.Length)
            {
                Array.Resize(ref _decided, Math.Max(_decided.Length * 2, t + 1));
            }
            if (_decided[t] == 0)
            {
                _decided[t] = matches(type.Name) ? (sbyte)1 : (sbyte)-1;
            }
            if (_decided[t] < 0)
            {
                return;
            }
            Matched++;
            MatchedBytes += size;
            if (limit == 0)
            {
                return;
            }
            if (Top.Count < limit)
            {
                Top.Enqueue(new ObjectInstance(address, type.Name, size, null), (size, address));
            }
            else if (Top.TryPeek(out _, out (ulong Size, ulong Address) smallest) && (size > smallest.Size || size == smallest.Size && address < smallest.Address))
            {
                Top.EnqueueDequeue(new ObjectInstance(address, type.Name, size, null), (size, address));
            }
        }
    }

    private readonly struct InstanceSink(InstanceSearch search) : IObjectSink
    {
        public void Object(ulong address, ulong size, HeapType type, DumpCursor cursor) => search.Consider(address, size, type);

        public void Free(ulong size)
        {
        }
    }
}

/// <summary>One worker's place in the dump: the run it's reading, looked up again only when a read leaves it, and a
/// buffer for the rare object whose bytes span two runs.</summary>
internal sealed unsafe class DumpCursor(DumpMemory memory) : IDisposable
{
    private ulong _start;
    private ulong _end;
    private byte* _base;
    private byte* _buffer;
    private ulong _capacity;

    public DumpMemory Memory => memory;

    /// <summary>The bytes [address, address + length) in place, or null when no single run holds them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* At(ulong address, ulong length) =>
        address >= _start && address < _end && length <= _end - address ? _base + (address - _start) : Move(address, length);

    private byte* Move(ulong address, ulong length)
    {
        byte* p = memory.Pointer(address, length, out ulong limit);
        if (p != null)
        {
            _start = address;
            _end = limit;
            _base = p;
        }
        return p;
    }

    /// <summary>An object's bytes, in place or copied when they span runs. When the dump lacks some, as it does for pages
    /// a process never touched, only the bytes up to the first gap: <paramref name="captured"/> says how many.</summary>
    public byte* Object(ulong address, ulong size, out ulong captured)
    {
        captured = size;
        byte* p = At(address, size);
        if (p != null)
        {
            return p;
        }
        captured = memory.Captured(address, size);
        p = At(address, captured);
        if (p != null || captured == 0)
        {
            return p;
        }
        if (captured > int.MaxValue)
        {
            throw new NotSupportedException($"Object 0x{address:x} spans the dump's ranges and is too large to copy.");
        }
        if (captured > _capacity)
        {
            NativeMemory.Free(_buffer);
            _buffer = (byte*)NativeMemory.Alloc((nuint)captured);
            _capacity = captured;
        }
        memory.TryCopy(address, new Span<byte>(_buffer, (int)captured));
        return _buffer;
    }

    public void Dispose()
    {
        NativeMemory.Free(_buffer);
        _buffer = null;
        _capacity = 0;
    }
}
