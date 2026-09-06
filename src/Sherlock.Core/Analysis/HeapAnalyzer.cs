using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>Walks the managed heap and aggregates object statistics by type.</summary>
public sealed class HeapAnalyzer(Snapshot snapshot)
{
    /// <summary>
    /// Groups live objects by type name with counts and total sizes, ordered by total size descending.
    /// Mirrors SOS <c>dumpheap -stat</c>.
    /// </summary>
    /// <param name="typeFilter">Optional case-insensitive substring filter on the type name.</param>
    public IReadOnlyList<HeapTypeStat> GetStatistics(string? typeFilter = null)
    {
        var stats = new Dictionary<string, (long Count, ulong Size)>(StringComparer.Ordinal);

        foreach (ClrObject obj in snapshot.Runtime.Heap.EnumerateObjects())
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
    /// Lists instances whose type name matches <paramref name="typeFilter"/>, returning the
    /// <paramref name="limit"/> largest by size descending, plus totals over all matches.
    /// </summary>
    /// <remarks>Uses cached type columns when available, with bounded top-K selection on either path.</remarks>
    public InstanceListing ListInstances(string typeFilter, int limit = 20, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeFilter);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.TryGetCachedHeapGraph()?.ListInstances(typeFilter, limit, cancellationToken) is { } listing)
        {
            var selected = new ObjectInstance[listing.Instances.Count];
            for (int i = 0; i < selected.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectInstance instance = listing.Instances[i];
                ClrObject obj = snapshot.Runtime.Heap.GetObject(instance.Address);
                selected[i] = instance with { Preview = Preview(obj, obj.Type) };
            }
            return listing with { Instances = selected };
        }

        // Min-heap keyed by size: the smallest of the current top-K sits at the front, evicted when a
        // larger instance arrives.
        var top = new PriorityQueue<ObjectInstance, ulong>(limit);
        long totalMatched = 0;
        ulong totalSize = 0;

        foreach (ClrObject obj in snapshot.Runtime.Heap.EnumerateObjects())
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

        var instances = new List<ObjectInstance>(top.Count);
        while (top.Count > 0)
        {
            instances.Add(top.Dequeue());
        }

        instances.Reverse();

        return new InstanceListing(instances, totalMatched, totalSize);
    }

    /// <summary>
    /// Finds string values occurring more than once, ordered by wasted memory: (count - 1) * size.
    /// </summary>
    public IReadOnlyList<DuplicateString> FindDuplicateStrings(int limit = 20, CancellationToken cancellationToken = default)
    {
        var groups = new Dictionary<string, (long Count, ulong TotalSize)>(StringComparer.Ordinal);

        foreach (ClrObject obj in snapshot.Runtime.Heap.EnumerateObjects())
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

    private static ObjectInstance BuildInstance(ClrObject obj, ClrType type, string name) =>
        new(obj.Address, name, obj.Size, Preview(obj, type));

    private static string? Preview(ClrObject obj, ClrType? type)
    {
        if (type?.IsString == true)
        {
            return obj.AsString(64);
        }
        return type?.IsArray == true ? "[]" : null;
    }
}
