using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    /// Finds string values occurring more than once, ordered by wasted memory: (count - 1) * size, then by address.
    /// </summary>
    /// <remarks>Values are compared by a hash of their characters, read in place by a parallel walk, and only the
    /// returned values are read in full. Scratch files grow with the number of strings; memory doesn't.</remarks>
    public IReadOnlyList<DuplicateString> FindDuplicateStrings(int limit = 20, CancellationToken cancellationToken = default)
    {
        long strings = snapshot.GetHistogram(cancellationToken).FirstOrDefault(t => t.TypeName == "System.String")?.Count ?? 0;
        Directory.CreateDirectory(snapshot.CacheDirectory);
        StringDuplicates.Group[] groups = StringDuplicates.Find(snapshot.GetDumpHeap(cancellationToken), strings, snapshot.CacheDirectory, limit, cancellationToken);
        ClrHeap heap = snapshot.Runtime.Heap;
        return [.. groups.Select(g => new DuplicateString(heap.GetObject(g.Address).AsString(MaxValueLength) ?? "", g.Count, (ulong)g.Count * g.Size, g.Wasted))];
    }

    private const int MaxValueLength = 65536;
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
