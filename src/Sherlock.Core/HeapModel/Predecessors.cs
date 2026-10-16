using System;
using System.Threading;

namespace Sherlock.Core.HeapModel;

/// <summary>Who references each object: the graph's reference edges reversed, sources in ascending order. Costs an
/// <c>int</c> per edge and per object.</summary>
internal sealed class Predecessors
{
    private readonly int[] _offsets;
    private readonly int[] _sources;

    private Predecessors(int[] offsets, int[] sources)
    {
        _offsets = offsets;
        _sources = sources;
    }

    /// <summary>The objects that reference <paramref name="objectId"/>. GC roots aren't included.</summary>
    public ReadOnlySpan<int> Of(int objectId) => _sources.AsSpan(_offsets[objectId], _offsets[objectId + 1] - _offsets[objectId]);

    public static Predecessors Build(HeapGraph graph, CancellationToken cancellationToken)
    {
        int count = graph.ObjectCount;
        var offsets = new int[count + 1];
        long edges = 0;
        for (int source = 0; source < count; source++)
        {
            if ((source & 0xffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            foreach (int target in graph.Successors(source))
            {
                offsets[target + 1]++;
            }
            edges += graph.Successors(source).Length;
        }
        if (edges > int.MaxValue)
        {
            throw new NotSupportedException($"{edges:N0} references exceed the 2.1B limit for finding GC roots.");
        }
        for (int i = 0; i < count; i++)
        {
            offsets[i + 1] += offsets[i];
        }

        var sources = new int[edges];
        var next = new int[count];
        Array.Copy(offsets, next, count);
        for (int source = 0; source < count; source++)
        {
            if ((source & 0xffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            foreach (int target in graph.Successors(source))
            {
                sources[next[target]++] = source;
            }
        }
        return new Predecessors(offsets, sources);
    }
}
