using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.HeapModel;

namespace Sherlock.Core.Analysis;

/// <summary>Lists instances and finds duplicate strings.</summary>
public sealed class HeapAnalyzer(Snapshot snapshot)
{
    /// <summary>
    /// Lists instances whose type name matches <paramref name="typeFilter"/>, returning the
    /// <paramref name="limit"/> largest by size descending, plus totals over all matches.
    /// </summary>
    /// <remarks>Uses the cached graph's type columns when there are any; otherwise walks the heap in parallel. Either way
    /// memory grows with the limit, not the matches.</remarks>
    /// <param name="exact">Match the full type name exactly instead of any name containing <paramref name="typeFilter"/>.</param>
    public InstanceListing ListInstances(string typeFilter, int limit = 20, CancellationToken cancellationToken = default, bool exact = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeFilter);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        cancellationToken.ThrowIfCancellationRequested();
        InstanceListing listing = snapshot.TryGetCachedHeapGraph(cancellationToken)?.ListInstances(typeFilter, limit, cancellationToken, exact)
            ?? HeapWalk.Instances(snapshot.GetDumpHeap(cancellationToken),
                name => exact ? name == typeFilter : name.Contains(typeFilter, StringComparison.OrdinalIgnoreCase), limit, cancellationToken);

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

    private const int PreviewLength = 256;

    // Strings are escaped so control characters in the dump can't drive the terminal.
    private static string? Preview(ClrObject obj, ClrType? type)
    {
        if (type?.IsString == true)
        {
            return obj.AsString(PreviewLength + 1) is { } text ? Printable.Escape(text, PreviewLength, '"') : null;
        }
        return type?.IsArray == true ? $"length {obj.AsArray().Length.ToString("N0", CultureInfo.InvariantCulture)}" : null;
    }
}
