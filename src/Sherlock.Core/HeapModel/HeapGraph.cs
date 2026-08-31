using System;

namespace Sherlock.Core.HeapModel;

/// <summary>A dense-id managed object graph with CSR reference edges.</summary>
public sealed class HeapGraph : IDisposable
{
    /// <summary>Ascending object addresses; the index is the object id.</summary>
    public ReadOnlyMemory<ulong> Addresses { get; }

    /// <summary>Shallow size per object id.</summary>
    public ReadOnlyMemory<uint> Sizes { get; }

    /// <summary>CSR row offsets, including the synthetic root row and terminator.</summary>
    public ReadOnlyMemory<long> Offsets { get; }

    /// <summary>Chunked CSR successor ids.</summary>
    public EdgeColumn Edges { get; }

    /// <summary>Per-object index into <see cref="TypeNames"/>, when available.</summary>
    public ReadOnlyMemory<int>? TypeIds { get; }

    /// <summary>Distinct type names, indexed by <see cref="TypeIds"/>.</summary>
    public string[]? TypeNames { get; }

    public bool HasTypes => TypeIds is not null;

    /// <summary>Free-space bytes excluded from the object graph.</summary>
    public ulong FreeBytes { get; }

    /// <summary>Free-space blocks excluded from the object graph.</summary>
    public long FreeCount { get; }

    internal ReadOnlyMemory<HeapRootRecord> Roots { get; }

    // Keeps mmap-backed columns alive.
    private readonly IDisposable? _backing;

    private AddressIndex? _index;
    private bool _disposed;

    public HeapGraph(ReadOnlyMemory<ulong> addresses, ReadOnlyMemory<uint> sizes, ReadOnlyMemory<long> offsets, EdgeColumn edges,
        ReadOnlyMemory<int>? typeIds = null, string[]? typeNames = null, ulong freeBytes = 0, long freeCount = 0,
        IDisposable? backing = null)
        : this(addresses, sizes, offsets, edges, typeIds, typeNames, freeBytes, freeCount, InferRoots(offsets, edges), backing)
    {
    }

    internal HeapGraph(ReadOnlyMemory<ulong> addresses, ReadOnlyMemory<uint> sizes, ReadOnlyMemory<long> offsets, EdgeColumn edges,
        ReadOnlyMemory<int>? typeIds, string[]? typeNames, ulong freeBytes, long freeCount,
        ReadOnlyMemory<HeapRootRecord> roots, IDisposable? backing = null)
    {
        if (offsets.Length != addresses.Length + 2)
        {
            throw new ArgumentException($"offsets length {offsets.Length} must be object count {addresses.Length} + 2 (incl. synthetic root).");
        }
        if ((typeIds is null) != (typeNames is null))
        {
            throw new ArgumentException("typeIds and typeNames must both be present or both absent.");
        }
        if (typeIds is not null && typeIds.Value.Length != addresses.Length)
        {
            throw new ArgumentException($"typeIds length {typeIds.Value.Length} must equal object count {addresses.Length}.");
        }
        if (sizes.Length != addresses.Length)
        {
            throw new ArgumentException($"sizes length {sizes.Length} must equal object count {addresses.Length}.");
        }
        foreach (HeapRootRecord root in roots.Span)
        {
            if ((uint)root.ObjectId >= (uint)addresses.Length)
            {
                throw new ArgumentException($"root object id {root.ObjectId} is outside the graph.");
            }
        }
        Addresses = addresses;
        Sizes = sizes;
        Offsets = offsets;
        Edges = edges;
        TypeIds = typeIds;
        TypeNames = typeNames;
        FreeBytes = freeBytes;
        FreeCount = freeCount;
        Roots = roots;
        _backing = backing;
    }

    /// <summary>Creates a single-chunk in-memory graph.</summary>
    public HeapGraph(ReadOnlyMemory<ulong> addresses, ReadOnlyMemory<uint> sizes, int[] offsets, int[] edges,
        ReadOnlyMemory<int>? typeIds = null, string[]? typeNames = null, ulong freeBytes = 0, long freeCount = 0)
        : this(addresses, sizes, WidenOffsets(offsets), new EdgeColumn(edges), typeIds, typeNames, freeBytes, freeCount)
    {
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
        return (_index ??= new AddressIndex(Addresses)).IndexOf(address);
    }

    /// <summary>Returns an object's type name when stored in the graph.</summary>
    public string? TypeNameOf(int objectId)
    {
        ThrowIfDisposed();
        return TypeIds is { } ids && TypeNames is { } names ? names[ids.Span[objectId]] : null;
    }

    private ulong _contentHash;
    private bool _hashed;

    /// <summary>Deterministic sampled fingerprint for derived-cache invalidation.</summary>
    public ulong ContentHash
    {
        get
        {
            ThrowIfDisposed();
            if (_hashed)
            {
                return _contentHash;
            }

            const ulong prime = 1099511628211UL;
            ulong h = 1469598103934665603UL;

            void Mix(ulong v) { for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (i * 8)); h *= prime; } }

            ReadOnlySpan<ulong> addr = Addresses.Span;
            Mix((ulong)addr.Length);
            Mix((ulong)Edges.Count);
            Mix((ulong)Offsets.Length);
            Mix(FreeBytes);
            Mix((ulong)Roots.Length);
            if (addr.Length > 0) { Mix(addr[0]); Mix(addr[^1]); }

            SampleU64(addr, Mix);
            Edges.Sample(4096, v => Mix((uint)v));
            foreach (HeapRootRecord root in Roots.Span)
            {
                Mix((uint)root.ObjectId);
                Mix(root.Address);
                Mix((uint)root.Kind);
                Mix(root.IsInterior ? 1u : 0u);
                Mix(root.IsPinned ? 1u : 0u);
            }

            _contentHash = h;
            _hashed = true;
            return _contentHash;

            static void SampleU64(ReadOnlySpan<ulong> a, Action<ulong> mix)
            {
                int step = System.Math.Max(1, a.Length / 4096);
                for (int i = 0; i < a.Length; i += step) mix(a[i]);
            }
        }
    }

    /// <summary>Builds a type histogram without ClrMD when type columns are present.</summary>
    public (string TypeName, long Count, ulong TotalSize)[]? Histogram()
    {
        ThrowIfDisposed();
        if (TypeIds is not { } idsMem || TypeNames is not { } names)
        {
            return null;
        }
        ReadOnlySpan<int> ids = idsMem.Span;
        ReadOnlySpan<uint> sizes = Sizes.Span;

        var counts = new long[names.Length];
        var bytes = new ulong[names.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            int t = ids[i];
            counts[t]++;
            bytes[t] += sizes[i];
        }

        var result = new (string, long, ulong)[names.Length + (FreeBytes > 0 ? 1 : 0)];
        for (int t = 0; t < names.Length; t++)
        {
            result[t] = (names[t], counts[t], bytes[t]);
        }
        if (FreeBytes > 0)
        {
            result[names.Length] = ("Free", FreeCount, FreeBytes);
        }
        return result;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
