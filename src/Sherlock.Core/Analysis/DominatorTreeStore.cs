using System;
using System.IO;
using Sherlock.Core.HeapModel;
using Sherlock.Core.Storage;

namespace Sherlock.Core.Analysis;

/// <summary>Persists the columns derived by dominator analysis.</summary>
public static class DominatorTreeStore
{
    private const uint Version = 4;

    public static void Save(string path, DominatorAnalyzer.DominatorResult result, Guid graphId)
    {
        var writer = new ContainerWriter(SlabKind.Dominators, Version);
        writer.AddRecords(SectionType.DomMeta, [graphId]);
        writer.AddMemory(SectionType.DomIdom, result.Idom);
        writer.AddMemory(SectionType.DomRetained, result.Retained);
        writer.AddMemory(SectionType.DomChildOffsets, result.ChildOffsets);
        writer.AddMemory(SectionType.DomChildren, result.Children);
        writer.Save(path);
    }

    /// <summary>Opens a tree computed from <paramref name="graph"/>, or null when it was computed from another.
    /// Columns stay in the mapping, which the tree owns.</summary>
    public static DominatorTree? Load(string path, HeapGraph graph)
    {
        SlabFile slab = SlabFile.Open(path, SlabKind.Dominators, Version);
        try
        {
            Column<Guid> meta = slab.GetColumn<Guid>(SectionType.DomMeta);
            if (meta.Length != 1 || meta[0] != graph.Id)
            {
                slab.Dispose();
                return null;
            }
            var result = new DominatorAnalyzer.DominatorResult(
                slab.GetColumn<int>(SectionType.DomIdom).AsMemory(),
                slab.GetColumn<ulong>(SectionType.DomRetained).AsMemory(),
                slab.GetColumn<long>(SectionType.DomChildOffsets).AsMemory(),
                slab.GetColumn<int>(SectionType.DomChildren).AsMemory());
            if (result.Idom.Length != graph.NodeCount || result.Retained.Length != graph.NodeCount ||
                result.ChildOffsets.Length != graph.NodeCount + 1 || result.ChildOffsets.Span[^1] != result.Children.Length)
            {
                throw new InvalidDataException("The dominator tree doesn't fit its graph.");
            }
            return new DominatorTree(graph, result, slab);
        }
        catch
        {
            slab.Dispose();
            throw;
        }
    }
}
