using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>Walks the managed heap and aggregates object statistics by type.</summary>
public sealed class HeapAnalyzer(DumpSession session)
{
    /// <summary>
    /// Groups every live object on the heap by type name, returning counts and
    /// total sizes ordered by total size descending. Mirrors SOS <c>dumpheap -stat</c>.
    /// </summary>
    /// <param name="typeFilter">Optional case-insensitive substring filter on the type name.</param>
    public IReadOnlyList<HeapTypeStat> GetStatistics(string? typeFilter = null)
    {
        var stats = new Dictionary<string, (long Count, ulong Size)>(StringComparer.Ordinal);

        foreach (ClrObject obj in session.Runtime.Heap.EnumerateObjects())
        {
            if (obj.Type is null)
            {
                continue;
            }

            string name = obj.Type.Name ?? "<unknown>";
            if (typeFilter is not null && !name.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ref var entry = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(stats, name, out _);
            entry.Count++;
            entry.Size += obj.Size;
        }

        return stats
            .Select(kvp => new HeapTypeStat(kvp.Key, kvp.Value.Count, kvp.Value.Size))
            .OrderByDescending(s => s.TotalSize)
            .ToList();
    }

    /// <summary>
    /// Lists individual object instances whose type name matches
    /// <paramref name="typeFilter"/>, returning the <paramref name="limit"/>
    /// largest by size (descending), along with totals over all matches.
    /// </summary>
    /// <remarks>
    /// Uses a bounded min-heap so memory stays O(limit) even when millions of
    /// objects match (e.g. listing every <c>System.String</c>).
    /// </remarks>
    public InstanceListing ListInstances(string typeFilter, int limit = 20, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeFilter);

        // Min-heap keyed by size: the smallest of the current top-K sits at the
        // front, ready to be evicted when a larger instance arrives.
        var top = new PriorityQueue<ObjectInstance, ulong>(limit);
        long totalMatched = 0;
        ulong totalSize = 0;

        foreach (ClrObject obj in session.Runtime.Heap.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();

            ClrType? type = obj.Type;
            if (type is null || obj.IsFree)
            {
                continue;
            }

            string name = type.Name ?? "<unknown>";
            if (!name.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            totalMatched++;
            totalSize += obj.Size;

            if (limit <= 0)
            {
                continue;
            }

            if (top.Count < limit)
            {
                top.Enqueue(BuildInstance(obj, type, name), obj.Size);
            }
            else if (obj.Size > top.Peek().Size)
            {
                top.EnqueueDequeue(BuildInstance(obj, type, name), obj.Size);
            }
        }

        // Drain the heap and present largest-first.
        var instances = new List<ObjectInstance>(top.Count);
        while (top.Count > 0)
            instances.Add(top.Dequeue());
        instances.Reverse();

        return new InstanceListing(instances, totalMatched, totalSize);
    }

    /// <summary>
    /// Finds string values that occur more than once, ordered by wasted memory
    /// (the dotMemory "duplicate strings" inspection). Wasted = (count - 1) × size.
    /// </summary>
    public IReadOnlyList<DuplicateString> FindDuplicateStrings(int limit = 20, CancellationToken cancellationToken = default)
    {
        var groups = new Dictionary<string, (long Count, ulong TotalSize)>(StringComparer.Ordinal);

        foreach (ClrObject obj in session.Runtime.Heap.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (obj.Type?.IsString != true)
            {
                continue;
            }

            string? value = obj.AsString(65536);
            if (value is null)
            {
                continue;
            }

            ref var entry = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(groups, value, out _);
            entry.Count++;
            entry.TotalSize += obj.Size;
        }

        return groups
            .Where(g => g.Value.Count > 1)
            .Select(g =>
            {
                ulong unit = g.Value.TotalSize / (ulong)g.Value.Count;
                return new DuplicateString(g.Key, g.Value.Count, g.Value.TotalSize, unit * (ulong)(g.Value.Count - 1));
            })
            .OrderByDescending(d => d.WastedBytes)
            .Take(limit)
            .ToList();
    }

    private static ObjectInstance BuildInstance(ClrObject obj, ClrType type, string name)
    {
        string? preview = null;
        if (type.IsString)
        {
            preview = obj.AsString(64);
        }
        else if (type.IsArray)
        {
            preview = "[]";
        }

        return new ObjectInstance(obj.Address, name, obj.Size, preview);
    }
}
