using System;
using System.IO;
using Sherlock.Core.HeapModel;
using Sherlock.Core.Storage;

namespace Sherlock.Core.Analysis;

/// <summary>Persists the columns derived by dominator analysis.</summary>
public static class DominatorTreeStore
{
    private const uint Version = 1;

    public static void Save(string path, DominatorAnalyzer.DominatorResult result, ulong graphContentHash)
    {
        var writer = new ContainerWriter(SlabKind.Dominators, Version);
        writer.AddRecords<ulong>(SectionType.DomMeta, [graphContentHash]);
        writer.AddMemory<int>(SectionType.DomNodeByRpo, result.NodeByRpo);
        writer.AddMemory<ulong>(SectionType.DomRetained, result.Retained);
        writer.AddMemory<int>(SectionType.DomIdom, result.Idom);
        writer.Save(path);
    }

    /// <summary>Loads a result built from <paramref name="graph"/>, or null when it was built from another, and
    /// reconstructs graph-owned columns.</summary>
    public static DominatorAnalyzer.DominatorResult? Load(string path, HeapGraph graph)
    {
        using SlabFile slab = SlabFile.Open(path, SlabKind.Dominators, Version);
        Column<ulong> meta = slab.GetColumn<ulong>(SectionType.DomMeta);
        if (meta.Length < 1 || meta[0] != graph.ContentHash)
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
