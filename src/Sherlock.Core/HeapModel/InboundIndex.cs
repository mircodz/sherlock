using System;
using System.Buffers;
using System.IO;
using System.Threading;
using Sherlock.Core.Storage;

namespace Sherlock.Core.HeapModel;

/// <summary>
/// Who references each node: the graph's edges reversed, kept in a file beside the graph. A node's referrers are in
/// ascending order, and an object referenced from the synthetic root lists the root among them. Built in two passes
/// over the edges, counting and then placing, straight into the mapped file.
/// </summary>
internal sealed unsafe class InboundIndex : IDisposable
{
    internal const uint Version = 1;

    private readonly SlabFile _slab;
    private readonly long* _offsets;
    private readonly int* _sources;

    private InboundIndex(SlabFile slab, int nodeCount, long* offsets, int* sources)
    {
        _slab = slab;
        NodeCount = nodeCount;
        _offsets = offsets;
        _sources = sources;
    }

    public int NodeCount { get; }

    /// <summary>The nodes that reference <paramref name="node"/>, ascending.</summary>
    public ReadOnlySpan<int> Of(int node)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)node, (uint)NodeCount, nameof(node));
        long start = _offsets[node];
        return new ReadOnlySpan<int>(_sources + start, (int)(_offsets[node + 1] - start));
    }

    public void Dispose() => _slab.Dispose();

    /// <summary>Opens the index of <paramref name="graph"/> at <paramref name="path"/>, building it there first when
    /// it's missing or belongs to another graph.</summary>
    public static InboundIndex Open(HeapGraph graph, string path, CancellationToken cancellationToken)
    {
        if (TryLoad(path, graph) is { } cached)
        {
            return cached;
        }
        Build(graph, path, cancellationToken);
        return Load(path, graph) ?? throw new InvalidDataException($"The reference index written to '{path}' doesn't match its graph.");
    }

    public static void Build(HeapGraph graph, string path, CancellationToken cancellationToken)
    {
        int nodes = graph.NodeCount;
        ReadOnlySpan<long> rows = graph.Offsets.Span;
        using var slab = new SlabBuilder(path, sectionCount: 3);
        long metaAt = slab.Place(SectionType.InboundMeta, sizeof(Guid), 1);
        long offsetsAt = slab.Place(SectionType.InboundOffsets, sizeof(long), nodes + 1L);
        long sourcesAt = slab.Place(SectionType.InboundSources, sizeof(int), rows[nodes]);
        byte* file = slab.Map();
        *(Guid*)(file + metaAt) = graph.Id;
        long* offsets = (long*)(file + offsetsAt);
        int* sources = (int*)(file + sourcesAt);

        int* edges = graph.Edges.Pin(out MemoryHandle pin);
        using (pin)
        {
            for (int source = 0; source < nodes; source++)
            {
                if ((source & 0xffff) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                for (long e = rows[source]; e < rows[source + 1]; e++)
                {
                    offsets[edges[e] + 1]++;
                }
            }
            for (int node = 0; node < nodes; node++)
            {
                offsets[node + 1] += offsets[node];
            }

            // Each node's start is its cursor while placing, ending at the next node's start; shifting restores them.
            for (int source = 0; source < nodes; source++)
            {
                if ((source & 0xffff) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                for (long e = rows[source]; e < rows[source + 1]; e++)
                {
                    sources[offsets[edges[e]]++] = source;
                }
            }
            Buffer.MemoryCopy(offsets, offsets + 1, (long)nodes * sizeof(long), (long)nodes * sizeof(long));
            offsets[0] = 0;
        }
        slab.Commit(SlabKind.Inbound, Version);
    }

    /// <summary>Opens the index at <paramref name="path"/>, or returns null when it was built from another graph.</summary>
    public static InboundIndex? Load(string path, HeapGraph graph)
    {
        SlabFile slab = SlabFile.Open(path, SlabKind.Inbound, Version);
        try
        {
            Column<Guid> meta = slab.GetColumn<Guid>(SectionType.InboundMeta);
            if (meta.Length != 1 || meta[0] != graph.Id)
            {
                slab.Dispose();
                return null;
            }
            Column<long> offsets = slab.GetColumn<long>(SectionType.InboundOffsets);
            Column<int> sources = slab.GetColumn<int>(SectionType.InboundSources);
            if (offsets.Length != graph.NodeCount + 1L || offsets[0] != 0 || offsets[offsets.Length - 1] != sources.Length)
            {
                throw new InvalidDataException("The reference index doesn't fit its graph.");
            }
            return new InboundIndex(slab, graph.NodeCount, offsets.Pointer, sources.Pointer);
        }
        catch
        {
            slab.Dispose();
            throw;
        }
    }

    private static InboundIndex? TryLoad(string path, HeapGraph graph)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return Load(path, graph);
        }
        catch (InvalidDataException)
        {
            return null; // rebuilt over it
        }
    }
}
