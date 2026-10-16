using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Sherlock.Core.HeapModel;

/// <summary>A dense-id managed object graph with CSR reference edges.</summary>
public sealed class HeapGraph : IDisposable
{
    /// <summary>Identifies this graph. Results derived from it record the id, so they're never applied to another
    /// graph of the same dump.</summary>
    public Guid Id { get; }

    /// <summary>Ascending object addresses; the index is the object id.</summary>
    public ReadOnlyMemory<ulong> Addresses { get; }

    /// <summary>Shallow size per object id; <see cref="uint.MaxValue"/> marks an object of 4 GiB or more, whose
    /// size <see cref="SizeOf"/> reads from a separate table.</summary>
    public ReadOnlyMemory<uint> Sizes { get; }

    /// <summary>Objects of 4 GiB or more, by ascending id.</summary>
    internal ReadOnlyMemory<LargeSize> LargeSizes { get; }

    /// <summary>CSR row offsets, including the synthetic root row and terminator.</summary>
    public ReadOnlyMemory<long> Offsets { get; }

    /// <summary>CSR successor ids.</summary>
    public EdgeColumn Edges { get; }

    /// <summary>Per-object index into <see cref="TypeNames"/>.</summary>
    public ReadOnlyMemory<int> TypeIds { get; }

    /// <summary>Distinct type names, indexed by <see cref="TypeIds"/>.</summary>
    public string[] TypeNames { get; }

    /// <summary>Free-space bytes excluded from the object graph.</summary>
    public ulong FreeBytes { get; }

    /// <summary>Free-space blocks excluded from the object graph.</summary>
    public long FreeCount { get; }

    internal ReadOnlyMemory<HeapRootRecord> Roots { get; }

    // Keeps mmap-backed columns alive.
    private readonly IDisposable? _backing;

    private readonly Once<AddressIndex> _index = new();
    private readonly Once<Predecessors> _predecessors = new();
    private bool _disposed;

    /// <summary>Creates an in-memory graph whose roots are the synthetic root's successors. Without type columns,
    /// every object is of one unknown type.</summary>
    public HeapGraph(ReadOnlyMemory<ulong> addresses, ReadOnlyMemory<uint> sizes, ReadOnlyMemory<long> offsets, EdgeColumn edges,
        ReadOnlyMemory<int>? typeIds = null, string[]? typeNames = null, ulong freeBytes = 0, long freeCount = 0)
        : this(Guid.NewGuid(), addresses, sizes, default, offsets, edges, TypeIdsOrDefault(typeIds, typeNames, addresses.Length),
            typeNames ?? ["<unknown>"], freeBytes, freeCount, InferRoots(offsets, edges))
    {
    }

    private static ReadOnlyMemory<int> TypeIdsOrDefault(ReadOnlyMemory<int>? typeIds, string[]? typeNames, int objectCount) =>
        (typeIds is null) != (typeNames is null)
            ? throw new ArgumentException("typeIds and typeNames must both be present or both absent.")
            : typeIds ?? new int[objectCount];

    /// <summary>Creates a single-chunk in-memory graph.</summary>
    public HeapGraph(ReadOnlyMemory<ulong> addresses, ReadOnlyMemory<uint> sizes, int[] offsets, int[] edges,
        ReadOnlyMemory<int>? typeIds = null, string[]? typeNames = null, ulong freeBytes = 0, long freeCount = 0)
        : this(addresses, sizes, WidenOffsets(offsets), new EdgeColumn(edges), typeIds, typeNames, freeBytes, freeCount)
    {
    }

    internal HeapGraph(Guid id, ReadOnlyMemory<ulong> addresses, ReadOnlyMemory<uint> sizes, ReadOnlyMemory<LargeSize> largeSizes,
        ReadOnlyMemory<long> offsets, EdgeColumn edges, ReadOnlyMemory<int> typeIds, string[] typeNames, ulong freeBytes, long freeCount,
        ReadOnlyMemory<HeapRootRecord> roots, IDisposable? backing = null)
    {
        if (offsets.Length != addresses.Length + 2)
        {
            throw new ArgumentException($"offsets length {offsets.Length} must be object count {addresses.Length} + 2 (incl. synthetic root).");
        }
        if (sizes.Length != addresses.Length || typeIds.Length != addresses.Length)
        {
            throw new ArgumentException($"sizes ({sizes.Length}) and type ids ({typeIds.Length}) must have one entry per object ({addresses.Length}).");
        }
        int previous = -1;
        foreach (LargeSize large in largeSizes.Span)
        {
            if (large.Id <= previous || large.Id >= addresses.Length || sizes.Span[large.Id] != uint.MaxValue)
            {
                throw new ArgumentException($"large object {large.Id} is out of order, outside the graph, or not marked in the size column.");
            }
            previous = large.Id;
        }
        foreach (HeapRootRecord root in roots.Span)
        {
            if ((uint)root.ObjectId >= (uint)addresses.Length)
            {
                throw new ArgumentException($"root object id {root.ObjectId} is outside the graph.");
            }
        }
        Id = id;
        Addresses = addresses;
        Sizes = sizes;
        LargeSizes = largeSizes;
        Offsets = offsets;
        Edges = edges;
        TypeIds = typeIds;
        TypeNames = typeNames;
        FreeBytes = freeBytes;
        FreeCount = freeCount;
        Roots = roots;
        _backing = backing;
    }

    private static long[] WidenOffsets(int[] offsets)
    {
        var wide = new long[offsets.Length];
        for (int i = 0; i < offsets.Length; i++) wide[i] = offsets[i];
        return wide;
    }

    private static HeapRootRecord[] InferRoots(ReadOnlyMemory<long> offsets, EdgeColumn edges)
    {
        ReadOnlySpan<long> rows = offsets.Span;
        if (rows.Length < 2)
        {
            return [];
        }
        long start = rows[^2];
        ReadOnlySpan<int> rootObjects = edges.Slice(start, checked((int)(rows[^1] - start)));
        var roots = new HeapRootRecord[rootObjects.Length];
        for (int i = 0; i < roots.Length; i++)
        {
            roots[i] = new HeapRootRecord(rootObjects[i], 0, Microsoft.Diagnostics.Runtime.ClrRootKind.None, false, false);
        }
        return roots;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _backing?.Dispose();
    }

    /// <summary>Number of real objects.</summary>
    public int ObjectCount => Addresses.Length;

    /// <summary>The synthetic root node id.</summary>
    public int Root => Addresses.Length;

    public int NodeCount => Addresses.Length + 1;

    /// <summary>Object references, or rooted objects for <see cref="Root"/>.</summary>
    public ReadOnlySpan<int> Successors(int node)
    {
        ThrowIfDisposed();
        ReadOnlySpan<long> offsets = Offsets.Span;
        long start = offsets[node];
        return Edges.Slice(start, (int)(offsets[node + 1] - start)); // out-degree always fits int
    }

    /// <summary>Returns the object id for an address, or -1.</summary>
    public int IndexOf(ulong address)
    {
        ThrowIfDisposed();
        return _index.Get(() => new AddressIndex(Addresses)).IndexOf(address);
    }

    /// <summary>Who references each object, built on first use and kept with the graph.</summary>
    internal Predecessors GetPredecessors(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _predecessors.Get(() => Predecessors.Build(this, cancellationToken), cancellationToken);
    }

    public string TypeNameOf(int objectId)
    {
        ThrowIfDisposed();
        return TypeNames[TypeIds.Span[objectId]];
    }

    /// <summary>An object's shallow size.</summary>
    public ulong SizeOf(int objectId)
    {
        ThrowIfDisposed();
        uint size = Sizes.Span[objectId];
        return size != uint.MaxValue ? size : LargeSizeOf(objectId);
    }

    private ulong LargeSizeOf(int objectId)
    {
        ReadOnlySpan<LargeSize> large = LargeSizes.Span;
        int lo = 0, hi = large.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (large[mid].Id == objectId)
            {
                return large[mid].Size;
            }
            if (large[mid].Id < objectId)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        throw new InvalidDataException($"Object {objectId} is marked as 4 GiB or larger but has no recorded size.");
    }

    /// <summary>Counts objects and bytes by type, plus free space.</summary>
    public (string TypeName, long Count, ulong TotalSize)[] Histogram()
    {
        ThrowIfDisposed();
        ReadOnlySpan<int> ids = TypeIds.Span;
        ReadOnlySpan<uint> sizes = Sizes.Span;

        var counts = new long[TypeNames.Length];
        var bytes = new ulong[TypeNames.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            int t = ids[i];
            counts[t]++;
            bytes[t] += sizes[i] != uint.MaxValue ? sizes[i] : LargeSizeOf(i);
        }

        var result = new (string, long, ulong)[TypeNames.Length + (FreeBytes > 0 ? 1 : 0)];
        for (int t = 0; t < TypeNames.Length; t++)
        {
            result[t] = (TypeNames[t], counts[t], bytes[t]);
        }
        if (FreeBytes > 0)
        {
            result[TypeNames.Length] = ("Free", FreeCount, FreeBytes);
        }
        return result;
    }

    /// <summary>Lists the largest matching objects without previews.</summary>
    public InstanceListing ListInstances(string typeFilter, int limit = 20, CancellationToken cancellationToken = default, bool exact = false)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(typeFilter);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        cancellationToken.ThrowIfCancellationRequested();
        string[] names = TypeNames;

        var matches = new bool[names.Length];
        bool anyMatch = false;
        for (int t = 0; t < names.Length; t++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            matches[t] = exact ? names[t] == typeFilter : names[t].Contains(typeFilter, StringComparison.OrdinalIgnoreCase);
            anyMatch |= matches[t];
        }
        if (!anyMatch)
        {
            return new InstanceListing([], 0, 0);
        }

        ReadOnlySpan<int> ids = TypeIds.Span;
        ReadOnlySpan<uint> sizes = Sizes.Span;
        var top = new PriorityQueue<int, ulong>();
        long totalMatched = 0;
        ulong totalSize = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!matches[ids[i]])
            {
                continue;
            }

            totalMatched++;
            ulong size = sizes[i] != uint.MaxValue ? sizes[i] : LargeSizeOf(i);
            totalSize += size;
            if (limit == 0)
            {
                continue;
            }
            if (top.Count < limit)
            {
                top.Enqueue(i, size);
            }
            else if (top.TryPeek(out _, out ulong smallest) && size > smallest)
            {
                top.EnqueueDequeue(i, size);
            }
        }

        var instances = new ObjectInstance[top.Count];
        ReadOnlySpan<ulong> addresses = Addresses.Span;
        for (int i = instances.Length - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int id = top.Dequeue();
            instances[i] = new ObjectInstance(addresses[id], names[ids[id]], SizeOf(id), null);
        }
        return new InstanceListing(instances, totalMatched, totalSize);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
