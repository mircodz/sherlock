using System;

namespace Sherlock.Core.HeapModel;

/// <summary>
/// The managed heap as a compact, DAC-free object graph: dense object ids (0..N-1), a sorted address
/// column, shallow sizes, and the outbound reference edges in CSR form (a row-offset array plus a flat
/// edge array). A synthetic <see cref="Root"/> node (id N) points at every GC root, so root membership
/// needs no separate structure and reachability is "reachable from Root".
///
/// This is the intermediate representation the V2 analysis stack computes over. It is deliberately
/// source-agnostic: <see cref="HeapGraphExtractor"/> builds it from a dump, and <see cref="HeapGraphStore"/>
/// round-trips it to a <c>.slab</c> — so once built it can be reloaded (memory-mapped) without touching
/// ClrMD again. Everything downstream (dominators, retained sizes, gcroot) is then pure int-array math.
/// </summary>
public sealed class HeapGraph
{
    /// <summary>Object addresses, ascending; the array index is the object's dense id.</summary>
    public ulong[] Addresses { get; }

    /// <summary>Shallow size per object id.</summary>
    public uint[] Sizes { get; }

    /// <summary>CSR row offsets. Length is <see cref="ObjectCount"/> + 2: node <c>i</c>'s successors are
    /// <c>Edges[Offsets[i]..Offsets[i+1]]</c>, and the synthetic root is node <see cref="Root"/>.</summary>
    public int[] Offsets { get; }

    /// <summary>CSR successor ids, sliced by <see cref="Offsets"/>.</summary>
    public int[] Edges { get; }

    private AddressIndex? _index; // lazy address→id acceleration

    public HeapGraph(ulong[] addresses, uint[] sizes, int[] offsets, int[] edges)
    {
        if (offsets.Length != addresses.Length + 2)
        {
            throw new ArgumentException($"offsets length {offsets.Length} must be object count {addresses.Length} + 2 (incl. synthetic root).");
        }
        Addresses = addresses;
        Sizes = sizes;
        Offsets = offsets;
        Edges = edges;
    }

    /// <summary>Number of real objects (excludes the synthetic root).</summary>
    public int ObjectCount => Addresses.Length;

    /// <summary>The synthetic root node id (its successors are the GC roots).</summary>
    public int Root => Addresses.Length;

    /// <summary>Total node count including the synthetic root.</summary>
    public int NodeCount => Addresses.Length + 1;

    /// <summary>The outbound edges of a node (object references, or GC roots for <see cref="Root"/>).</summary>
    public ReadOnlySpan<int> Successors(int node) =>
        Edges.AsSpan(Offsets[node], Offsets[node + 1] - Offsets[node]);

    /// <summary>The dense id of an object address, or -1 if there is no such object (a lazy
    /// <see cref="AddressIndex"/> built on first use).</summary>
    public int IndexOf(ulong address) => (_index ??= new AddressIndex(Addresses)).IndexOf(address);
}
