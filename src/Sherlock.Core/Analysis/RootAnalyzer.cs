using System.Collections.Generic;
using System.Threading;
using Sherlock.Core.HeapModel;
using Sherlock.Storage;

namespace Sherlock.Core.Analysis;

/// <summary>Finds the GC roots that keep an object alive. One breadth-first search walks references backwards from the
/// object, so every root that reaches it is found, each with a shortest path. Its arrays are scratch files in
/// <c>scratchDirectory</c>.</summary>
internal static unsafe class RootAnalyzer
{
    public static IReadOnlyList<GcRootPath> Find(HeapGraph graph, InboundIndex inbound, string scratchDirectory, ulong address,
        CancellationToken cancellationToken = default)
    {
        int target = graph.IndexOf(address);
        if (target < 0)
        {
            return [];
        }

        using var steps = new ScratchArray<int>(scratchDirectory, graph.ObjectCount);
        using (var queue = new ScratchArray<int>(scratchDirectory, graph.ObjectCount))
        {
            StepsTo(graph, inbound, target, steps.Pointer, queue.Pointer, cancellationToken);
        }
        var results = new List<GcRootPath>();
        foreach (HeapRootRecord root in graph.Roots.Span)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (steps.Pointer[root.ObjectId] == 0)
            {
                continue;
            }
            string kind = root.Kind == Microsoft.Diagnostics.Runtime.ClrRootKind.None ? "GC root" : root.Kind.ToString();
            results.Add(new GcRootPath(new GcRootInfo(root.Address, kind, root.IsInterior, root.IsPinned),
                PathFrom(graph, root.ObjectId, steps.Pointer)));
        }
        return results;
    }

    // For each object, one more than the fewest references from it to the target; 0 when it doesn't reach the target.
    private static void StepsTo(HeapGraph graph, InboundIndex inbound, int target, int* steps, int* queue, CancellationToken cancellationToken)
    {
        steps[target] = 1;
        queue[0] = target;
        for (int head = 0, tail = 1; head < tail; head++)
        {
            if ((head & 0xffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            int node = queue[head];
            foreach (int holder in inbound.Of(node))
            {
                if (holder != graph.Root && steps[holder] == 0)
                {
                    steps[holder] = steps[node] + 1;
                    queue[tail++] = holder;
                }
            }
        }
    }

    // Each step follows the first reference, in the object's own order, that is one step closer to the target.
    private static List<GcRootNode> PathFrom(HeapGraph graph, int start, int* steps)
    {
        var path = new List<GcRootNode>(steps[start]);
        for (int node = start; ; )
        {
            path.Add(new GcRootNode(graph.Addresses.Span[node], graph.TypeNameOf(node)));
            if (steps[node] == 1)
            {
                return path; // the target
            }
            foreach (int next in graph.Successors(node))
            {
                if (steps[next] == steps[node] - 1)
                {
                    node = next;
                    break;
                }
            }
        }
    }
}
