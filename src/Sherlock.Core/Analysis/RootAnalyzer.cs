using System.Collections.Generic;
using System.Threading;
using Sherlock.Core.HeapModel;

namespace Sherlock.Core.Analysis;

/// <summary>Finds the GC roots that keep an object alive. One breadth-first search walks references backwards from the
/// object, so every root that reaches it is found, each with a shortest path.</summary>
internal static class RootAnalyzer
{
    public static IReadOnlyList<GcRootPath> Find(HeapGraph graph, ulong address, CancellationToken cancellationToken = default)
    {
        int target = graph.IndexOf(address);
        if (target < 0)
        {
            return [];
        }

        int[] steps = StepsTo(graph, target, cancellationToken);
        var results = new List<GcRootPath>();
        foreach (HeapRootRecord root in graph.Roots.Span)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (steps[root.ObjectId] == 0)
            {
                continue;
            }
            string kind = root.Kind == Microsoft.Diagnostics.Runtime.ClrRootKind.None ? "GC root" : root.Kind.ToString();
            results.Add(new GcRootPath(new GcRootInfo(root.Address, kind, root.IsInterior, root.IsPinned),
                PathFrom(graph, root.ObjectId, steps)));
        }
        return results;
    }

    // For each object, one more than the fewest references from it to the target; 0 when it doesn't reach the target.
    private static int[] StepsTo(HeapGraph graph, int target, CancellationToken cancellationToken)
    {
        Predecessors predecessors = graph.GetPredecessors(cancellationToken);
        var steps = new int[graph.ObjectCount];
        steps[target] = 1;
        var queue = new Queue<int>();
        queue.Enqueue(target);
        for (int visited = 0; queue.TryDequeue(out int node); visited++)
        {
            if ((visited & 0xffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            foreach (int holder in predecessors.Of(node))
            {
                if (steps[holder] == 0)
                {
                    steps[holder] = steps[node] + 1;
                    queue.Enqueue(holder);
                }
            }
        }
        return steps;
    }

    // Each step follows the first reference, in the object's own order, that is one step closer to the target.
    private static List<GcRootNode> PathFrom(HeapGraph graph, int start, int[] steps)
    {
        var path = new List<GcRootNode>(steps[start]);
        for (int node = start; ; )
        {
            path.Add(new GcRootNode(graph.Addresses.Span[node], graph.TypeNameOf(node) ?? "<unknown>"));
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
