using System.IO;
using System.Runtime.InteropServices;
using Sherlock.Core.Storage;

namespace Sherlock.Core.HeapModel;

/// <summary>
/// Round-trips a <see cref="HeapGraph"/> to a self-describing <c>.slab</c> container: the columns are
/// written as zero-copy POD sections and read back the same way, so a persisted graph reloads without
/// re-walking the dump. (First cut materializes the columns into arrays on load; keeping them mapped for
/// truly out-of-core analysis, and streaming the writer for graphs &gt; 2&nbsp;GB, are follow-ups.)
/// </summary>
public static class HeapGraphStore
{
    private const ushort Version = 1;

    public static void Save(string path, HeapGraph graph)
    {
        var writer = new ContainerWriter();
        writer.AddRecords<ulong>(SectionType.GraphAddresses, Version, graph.Addresses);
        writer.AddRecords<uint>(SectionType.GraphSizes, Version, graph.Sizes);
        writer.AddRecords<int>(SectionType.GraphOffsets, Version, graph.Offsets);
        writer.AddRecords<int>(SectionType.GraphEdges, Version, graph.Edges);
        File.WriteAllBytes(path, writer.ToArray());
    }

    /// <summary>Loads a graph from <paramref name="path"/>, or null if the file is missing a section
    /// (a format mismatch — the caller should rebuild).</summary>
    public static HeapGraph? Load(string path)
    {
        using ContainerReader container = ContainerReader.Open(path);
        if (!container.TryGetSection(SectionType.GraphAddresses, out Section addresses) ||
            !container.TryGetSection(SectionType.GraphSizes, out Section sizes) ||
            !container.TryGetSection(SectionType.GraphOffsets, out Section offsets) ||
            !container.TryGetSection(SectionType.GraphEdges, out Section edges))
        {
            return null;
        }

        return new HeapGraph(
            addresses.AsRecords<ulong>().ToArray(),
            sizes.AsRecords<uint>().ToArray(),
            offsets.AsRecords<int>().ToArray(),
            edges.AsRecords<int>().ToArray());
    }
}
