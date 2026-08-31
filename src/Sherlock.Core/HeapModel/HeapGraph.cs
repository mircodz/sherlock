using System;

namespace Sherlock.Core.HeapModel;

/// <summary>
/// The managed heap as a compact, DAC-free object graph: dense object ids (0..N-1), a sorted address
/// column, shallow sizes, and outbound reference edges in CSR form (a row-offset array plus a flat edge
/// array). A synthetic <see cref="Root"/> node (id N) points at every GC root, so root membership needs
/// no separate structure and reachability is "reachable from Root".
///
/// The intermediate representation the V2 analysis stack computes over. Source-agnostic:
/// <see cref="HeapGraphExtractor"/> builds it from a dump, and <see cref="HeapGraphStore"/> round-trips
/// it to a <c>.slab</c>, so once built it reloads (memory-mapped) without touching ClrMD again.
/// Everything downstream (dominators, retained sizes, gcroot) is pure int-array math.
/// </summary>
public sealed class HeapGraph : IDisposable
{
    /// <summary>Object addresses, ascending; the array index is the object's dense id.</summary>
    public ReadOnlyMemory<ulong> Addresses { get; }

    /// <summary>Shallow size per object id.</summary>
    public ReadOnlyMemory<uint> Sizes { get; }

    /// <summary>CSR row offsets (global <c>long</c> edge positions, so the edge count can exceed the
    /// ~2.1B <c>int</c> ceiling). Length is <see cref="ObjectCount"/> + 2: node <c>i</c>'s successors are
    /// edges <c>[Offsets[i], Offsets[i+1])</c>, and the synthetic root is node <see cref="Root"/>.</summary>
    public ReadOnlyMemory<long> Offsets { get; }

    /// <summary>CSR successor ids, sliced by <see cref="Offsets"/>, chunked so it can exceed the ~2.1B
    /// single-array ceiling (see <see cref="EdgeColumn"/>).</summary>
    public EdgeColumn Edges { get; }

    /// <summary>Per-object type index into <see cref="TypeNames"/>, or null if the graph carries no type
    /// column (an older slab, or one built without types); callers then fall back to ClrMD.</summary>
    public ReadOnlyMemory<int>? TypeIds { get; }

    /// <summary>The distinct type names, indexed by <see cref="TypeIds"/>. Null iff <see cref="TypeIds"/>
    /// is null.</summary>
    public string[]? TypeNames { get; }

    /// <summary>True when the graph carries per-object types (so histogram / type-name resolution can be
    /// answered without ClrMD).</summary>
    public bool HasTypes => TypeIds is not null;

    /// <summary>Total free-space bytes on the heap (fragmentation). Free objects aren't real objects so
    /// they're excluded from the graph, but their total is kept so the histogram can report a "Free" row
    /// like a ClrMD walk (the doctor's fragmentation check relies on it).</summary>
    public ulong FreeBytes { get; }

    /// <summary>Number of free-space blocks (the count for the synthetic "Free" histogram row).</summary>
    public long FreeCount { get; }

    // When the columns are zero-copy views into a memory-mapped slab, this is the mapping that owns those
    // bytes; the graph keeps it alive and disposes it. Null for a freshly extracted in-memory graph.
    private readonly IDisposable? _backing;

    private AddressIndex? _index; // lazy address→id acceleration
    private bool _disposed;

    public HeapGraph(ReadOnlyMemory<ulong> addresses, ReadOnlyMemory<uint> sizes, ReadOnlyMemory<long> offsets, EdgeColumn edges,
        ReadOnlyMemory<int>? typeIds = null, string[]? typeNames = null, ulong freeBytes = 0, long freeCount = 0,
        IDisposable? backing = null)
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
        Addresses = addresses;
        Sizes = sizes;
        Offsets = offsets;
        Edges = edges;
        TypeIds = typeIds;
        TypeNames = typeNames;
        FreeBytes = freeBytes;
        FreeCount = freeCount;
        _backing = backing;
    }

    /// <summary>Array-friendly overload for an in-memory graph (fresh extract, hand-built test graphs):
    /// widens the <c>int</c> offsets to <c>long</c> and wraps the flat edge array in a single-chunk
    /// <see cref="EdgeColumn"/>. Under the ceiling by construction; a graph large enough to overflow is
    /// built via the chunked path.</summary>
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _backing?.Dispose();
    }

    /// <summary>Number of real objects (excludes the synthetic root).</summary>
    public int ObjectCount => Addresses.Length;

    /// <summary>The synthetic root node id (its successors are the GC roots).</summary>
    public int Root => Addresses.Length;

    /// <summary>Total node count including the synthetic root.</summary>
    public int NodeCount => Addresses.Length + 1;

    /// <summary>The outbound edges of a node (object references, or GC roots for <see cref="Root"/>).</summary>
    public ReadOnlySpan<int> Successors(int node)
    {
        ThrowIfDisposed();
        ReadOnlySpan<long> offsets = Offsets.Span;
        long start = offsets[node];
        return Edges.Slice(start, (int)(offsets[node + 1] - start)); // out-degree always fits int
    }

    /// <summary>The dense id of an object address, or -1 if there is no such object (a lazy
    /// <see cref="AddressIndex"/> built on first use).</summary>
    public int IndexOf(ulong address)
    {
        ThrowIfDisposed();
        return (_index ??= new AddressIndex(Addresses)).IndexOf(address);
    }

    /// <summary>The type name of an object id, or null if the graph carries no type column.</summary>
    public string? TypeNameOf(int objectId)
    {
        ThrowIfDisposed();
        return TypeIds is { } ids && TypeNames is { } names ? names[ids.Span[objectId]] : null;
    }

    private ulong _contentHash;
    private bool _hashed;

    /// <summary>A stable structural fingerprint used only to detect "this is a different graph" so a
    /// derived cache (the dominator tree) can be invalidated. NOT cryptographic and not a full-content
    /// hash (a full pass over 400M edges every reopen would defeat the point): it mixes object/edge
    /// counts, address endpoints, and a strided sample of the address and edge columns. Deterministic
    /// across processes (FNV-1a, never <see cref="System.HashCode"/>, which is per-process randomized), so
    /// it can be persisted and compared on reopen. Computed once, lazily.</summary>
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
            if (addr.Length > 0) { Mix(addr[0]); Mix(addr[^1]); }

            // Strided samples (~4096 points each), enough to catch any structural change cheaply.
            SampleU64(addr, Mix);
            Edges.Sample(4096, v => Mix((uint)v));

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

    /// <summary>Per-type (count, total shallow bytes), or null if the graph carries no type column.
    /// Computed directly off the graph, no ClrMD heap walk.</summary>
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
        // Report free space as a synthetic "Free" row, matching a ClrMD histogram (the fragmentation
        // check reads it). Free objects aren't in the graph, so this is the only place it appears.
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
