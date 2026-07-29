using System;
using System.IO;
using Sherlock.Core.HeapModel;
using Sherlock.Core.Storage;

namespace Sherlock.Core.Analysis;

/// <summary>
/// Round-trips the derived dominator result to a <c>.slab</c> beside the dump so reopening a snapshot
/// skips the ~seconds-long dominator recompute. To avoid duplicating data already in the graph slab,
/// it stores only the genuinely-derived columns — the RPO→object-id map, retained sizes, and immediate
/// dominators — keyed by the source graph's <see cref="HeapGraph.ContentHash"/>. The address and
/// own-size columns are NOT persisted: they're reconstructed from the graph's own columns on load
/// (via the RPO→id map), which roughly halves the file. On load the content-hash key is checked, so a
/// cache computed from a different graph is rejected and transparently recomputed.
/// </summary>
public static class DominatorTreeStore
{
    private const ushort Version = 2; // v2: store NodeByRpo instead of Address/Own (dedup vs graph)

    public static void Save(string path, DominatorAnalyzerV2.DominatorResult result, ulong graphContentHash)
    {
        var writer = new ContainerWriter();
        writer.AddRecords<ulong>(SectionType.DomMeta, Version, [graphContentHash]);
        writer.AddRecords<int>(SectionType.DomNodeByRpo, Version, result.NodeByRpo);
        writer.AddRecords<ulong>(SectionType.DomRetained, Version, result.Retained);
        writer.AddRecords<int>(SectionType.DomIdom, Version, result.Idom);
        File.WriteAllBytes(path, writer.ToArray());
    }

    /// <summary>Loads the cached dominator result, reconstructing the address/own-size columns from
    /// <paramref name="graph"/>. Returns null if the file is missing/stale/wrong-version or its key
    /// doesn't match the graph — meaning the caller should recompute.</summary>
    public static DominatorAnalyzerV2.DominatorResult? Load(string path, HeapGraph graph)
    {
        using SlabFile slab = SlabFile.Open(path);
        if (!slab.Has(SectionType.DomMeta) || !slab.Has(SectionType.DomNodeByRpo) ||
            !slab.Has(SectionType.DomRetained) || !slab.Has(SectionType.DomIdom))
        {
            return null;
        }
        Column<ulong> metaCol = slab.GetColumn<ulong>(SectionType.DomMeta);
        if (slab.SectionVersion(SectionType.DomMeta) != Version || metaCol.Length < 1 || metaCol[0] != graph.ContentHash)
        {
            return null; // stale format or a different graph — recompute
        }

        int[] nodeByRpo = ToArray(slab.GetColumn<int>(SectionType.DomNodeByRpo));
        ulong[] retained = ToArray(slab.GetColumn<ulong>(SectionType.DomRetained));
        int[] idom = ToArray(slab.GetColumn<int>(SectionType.DomIdom));

        // Reconstruct the RPO-indexed address + own-size columns from the graph (no re-storage).
        int m = nodeByRpo.Length;
        int root = graph.Root;
        ReadOnlySpan<ulong> gAddr = graph.Addresses.Span;
        ReadOnlySpan<uint> gSize = graph.Sizes.Span;
        var address = new ulong[m];
        var own = new ulong[m];
        for (int rpo = 0; rpo < m; rpo++)
        {
            int node = nodeByRpo[rpo];
            if (node != root && node >= 0)
            {
                address[rpo] = gAddr[node];
                own[rpo] = gSize[node];
            }
        }

        return new DominatorAnalyzerV2.DominatorResult(address, own, retained, idom, nodeByRpo);
    }

    // These derived columns are small (bounded by reachable node count, comfortably int-sized), so
    // materializing them into an array is fine — the analysis indexes them densely.
    private static T[] ToArray<T>(Column<T> col) where T : unmanaged
    {
        var arr = new T[checked((int)col.Length)];
        col.CopyTo(0, arr);
        return arr;
    }
}
