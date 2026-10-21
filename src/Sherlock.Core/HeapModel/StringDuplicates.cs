using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Threading;
using Sherlock.Storage;

namespace Sherlock.Core.HeapModel;

/// <summary>
/// Finds the strings whose value occurs more than once, without holding the values. The parallel walk hashes each
/// string's characters in place in the mapped dump and records its hash, size and address in a scratch file; records
/// are then split into buckets by hash, and each bucket is sorted and scanned for repeats on its own worker. Memory
/// stays flat however many distinct strings there are; a 128-bit hash makes telling two values apart by hash safe.
/// </summary>
internal static unsafe class StringDuplicates
{
    private const int Buckets = 256;
    private const int Batch = 4096;

    /// <summary>A repeated value: its lowest-addressed copy, how many copies there are, and each copy's size.</summary>
    public readonly record struct Group(ulong Address, long Count, ulong Size)
    {
        public ulong Wasted => (ulong)(Count - 1) * Size;
    }

    /// <summary>The <paramref name="limit"/> groups wasting the most bytes, most first, then by address.
    /// <paramref name="strings"/> is how many strings the heap holds, from its histogram.</summary>
    public static Group[] Find(DumpHeap heap, long strings, string scratchDirectory, int limit, CancellationToken cancellationToken)
    {
        if (strings < 2 || limit <= 0)
        {
            return [];
        }
        using var records = new ScratchArray<Record>(scratchDirectory, strings);
        long count = Hash(heap, records, cancellationToken);
        using var bucketed = new ScratchArray<Record>(scratchDirectory, count);
        long[] starts = Partition(records.Pointer, bucketed.Pointer, count, cancellationToken);
        return Scan(bucketed.Pointer, starts, limit, cancellationToken);
    }

    // A string's hash, then its address, so a sorted bucket lists each value's copies together, lowest address first.
    private readonly record struct Record(ulong High, ulong Low, ulong Address, ulong Size) : IComparable<Record>
    {
        public int CompareTo(Record other) =>
            High != other.High ? High.CompareTo(other.High) : Low != other.Low ? Low.CompareTo(other.Low) : Address.CompareTo(other.Address);
    }

    // Each worker hashes into a local batch and copies it into the next free records; returns how many there are.
    private static long Hash(DumpHeap heap, ScratchArray<Record> records, CancellationToken cancellationToken)
    {
        HeapSegment[] largestFirst = [.. heap.Segments.OrderByDescending(s => s.Bytes)];
        long written = 0;
        int next = -1;
        HeapWalk.Run(Environment.ProcessorCount, (_, stop) =>
        {
            var sink = new HashSink(records, new Record[Batch], n => Interlocked.Add(ref written, n) - n);
            var types = new TypeCache(heap.Types);
            using var cursor = new DumpCursor(heap.Memory);
            for (int i; (i = Interlocked.Increment(ref next)) < largestFirst.Length;)
            {
                HeapWalk.Walk(largestFirst[i], cursor, largestFirst[i].Start, ulong.MaxValue, types, ref sink, stop);
            }
            sink.Flush();
        }, cancellationToken);
        return written;
    }

    private struct HashSink(ScratchArray<Record> records, Record[] batch, Func<int, long> reserve) : IObjectSink
    {
        private int _count;

        public void Object(ulong address, ulong size, HeapType type, DumpCursor cursor)
        {
            if (!type.IsString)
            {
                return;
            }
            // A string is its method table, a 4-byte length, then UTF-16 characters. One the dump holds only part of
            // has no value to compare.
            byte* bytes = cursor.Object(address, size, out ulong captured);
            if (bytes == null || captured < 12)
            {
                return;
            }
            ulong length = *(uint*)(bytes + 8) * 2UL;
            if (12 + length > captured)
            {
                return;
            }
            UInt128 hash = XxHash128.HashToUInt128(new ReadOnlySpan<byte>(bytes + 12, (int)length));
            batch[_count++] = new Record((ulong)(hash >> 64), (ulong)hash, address, size);
            if (_count == batch.Length)
            {
                Flush();
            }
        }

        public void Free(ulong size)
        {
        }

        public void Flush()
        {
            if (_count == 0)
            {
                return;
            }
            long at = reserve(_count);
            if (at + _count > records.Length)
            {
                throw new InvalidDataException($"The heap holds more strings than its histogram counts ({records.Length:N0}).");
            }
            batch.AsSpan(0, _count).CopyTo(new Span<Record>(records.Pointer + at, _count));
            _count = 0;
        }
    }

    // Splits the records into buckets by the top byte of their hash, each worker moving a contiguous share; returns
    // where each bucket starts, then the total.
    private static long[] Partition(Record* records, Record* bucketed, long count, CancellationToken cancellationToken)
    {
        int workers = Environment.ProcessorCount;
        long share = (count + workers - 1) / workers;
        var counts = new long[workers][];
        HeapWalk.Run(workers, (w, stop) =>
        {
            var mine = counts[w] = new long[Buckets];
            long end = Math.Min(count, (w + 1) * share);
            for (long i = w * share; i < end; i++)
            {
                mine[records[i].High >> 56]++;
            }
        }, cancellationToken);

        var starts = new long[Buckets + 1];
        long[][] cursors = [.. Enumerable.Range(0, workers).Select(_ => new long[Buckets])];
        long at = 0;
        for (int b = 0; b < Buckets; b++)
        {
            starts[b] = at;
            for (int w = 0; w < workers; w++)
            {
                cursors[w][b] = at;
                at += counts[w][b];
            }
        }
        starts[Buckets] = at;

        HeapWalk.Run(workers, (w, stop) =>
        {
            long[] cursor = cursors[w];
            long end = Math.Min(count, (w + 1) * share);
            for (long i = w * share; i < end; i++)
            {
                bucketed[cursor[records[i].High >> 56]++] = records[i];
            }
        }, cancellationToken);
        return starts;
    }

    // Sorts each bucket and scans its runs of one hash, keeping each worker's largest groups.
    private static Group[] Scan(Record* bucketed, long[] starts, int limit, CancellationToken cancellationToken)
    {
        int workers = Environment.ProcessorCount;
        var tops = new PriorityQueue<Group, Group>[workers];
        int next = -1;
        HeapWalk.Run(workers, (w, stop) =>
        {
            var top = tops[w] = new PriorityQueue<Group, Group>(SmallestFirst.Instance);
            for (int b; (b = Interlocked.Increment(ref next)) < Buckets;)
            {
                stop.ThrowIfCancellationRequested();
                var bucket = new Span<Record>(bucketed + starts[b], checked((int)(starts[b + 1] - starts[b])));
                bucket.Sort();
                for (int i = 0, j; i < bucket.Length; i = j)
                {
                    for (j = i + 1; j < bucket.Length && bucket[j].High == bucket[i].High && bucket[j].Low == bucket[i].Low; j++)
                    {
                    }
                    if (j - i < 2)
                    {
                        continue;
                    }
                    var group = new Group(bucket[i].Address, j - i, bucket[i].Size);
                    if (top.Count < limit)
                    {
                        top.Enqueue(group, group);
                    }
                    else if (SmallestFirst.Instance.Compare(group, top.Peek()) > 0)
                    {
                        top.EnqueueDequeue(group, group);
                    }
                }
            }
        }, cancellationToken);

        return [.. tops.SelectMany(t => t.UnorderedItems.Select(item => item.Element))
            .OrderByDescending(g => g.Wasted).ThenBy(g => g.Address).Take(limit)];
    }

    // Less wasted first; of equal waste, the higher address first, so it's the one evicted.
    private sealed class SmallestFirst : IComparer<Group>
    {
        public static readonly SmallestFirst Instance = new();

        public int Compare(Group a, Group b) => a.Wasted != b.Wasted ? a.Wasted.CompareTo(b.Wasted) : b.Address.CompareTo(a.Address);
    }
}
