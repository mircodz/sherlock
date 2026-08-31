using System;
using System.IO;
using Sherlock.Core.HeapModel;
using Sherlock.Core.Storage;

namespace Sherlock.Core.Analysis;

/// <summary>Persists the columns derived by dominator analysis.</summary>
public static class DominatorTreeStore
{
    private const ushort Version = 2;

    public static void Save(string path, DominatorAnalyzer.DominatorResult result, ulong graphContentHash)
    {
        var writer = new ContainerWriter();
        writer.AddRecords(SectionType.DomMeta, Version, [graphContentHash]);
        writer.AddMemoryRecords<int>(SectionType.DomNodeByRpo, Version, result.NodeByRpo);
        writer.AddMemoryRecords<ulong>(SectionType.DomRetained, Version, result.Retained);
        writer.AddMemoryRecords<int>(SectionType.DomIdom, Version, result.Idom);
        writer.Save(path);
    }

    /// <summary>Loads a compatible result and reconstructs graph-owned columns.</summary>
    public static DominatorAnalyzer.DominatorResult? Load(string path, HeapGraph graph)
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
            return null;
        }

        int[] nodeByRpo = ToArray(slab.GetColumn<int>(SectionType.DomNodeByRpo));
        ulong[] retained = ToArray(slab.GetColumn<ulong>(SectionType.DomRetained));
        int[] idom = ToArray(slab.GetColumn<int>(SectionType.DomIdom));

        int m = nodeByRpo.Length;
        if (retained.Length != m || idom.Length != m || m == 0)
        {
            return null;
        }
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

        return new DominatorAnalyzer.DominatorResult(address, own, retained, idom, nodeByRpo);
    }

    private static T[] ToArray<T>(Column<T> col) where T : unmanaged
    {
        var arr = new T[checked((int)col.Length)];
        col.CopyTo(0, arr);
        return arr;
    }
}
