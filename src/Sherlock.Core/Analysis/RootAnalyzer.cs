using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>
/// Finds GC roots that keep an object alive. Implemented as a breadth-first
/// search outward from every GC root, following object references until the
/// target is reached. The first path found is the shortest in edges.
/// </summary>
/// <remarks>
/// This is a deliberately simple traversal: it visits objects reachable from
/// roots until it hits the target. For very large heaps this can be slow; a
/// future revision can swap in retained-size/dominator indexing.
/// </remarks>
public sealed class RootAnalyzer
{
    private readonly DumpSession _session;

    public RootAnalyzer(DumpSession session) => _session = session;

    /// <summary>
    /// Returns up to <paramref name="maxPaths"/> root paths that reach
    /// <paramref name="targetAddress"/>.
    /// </summary>
    public IReadOnlyList<GcRootPath> FindRoots(ulong targetAddress, int maxPaths = 1, CancellationToken cancellationToken = default)
    {
        ClrHeap heap = _session.Runtime.Heap;
        var results = new List<GcRootPath>();

        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();

            ulong rootObj = root.Object.Address;
            if (rootObj == 0)
                continue;

            List<GcRootNode>? path = BreadthFirstSearch(heap, rootObj, targetAddress, cancellationToken);
            if (path is not null)
            {
                results.Add(new GcRootPath(DescribeRoot(root), path));
                if (results.Count >= maxPaths)
                    break;
            }
        }

        return results;
    }

    private static List<GcRootNode>? BreadthFirstSearch(ClrHeap heap, ulong start, ulong target, CancellationToken cancellationToken)
    {
        var visited = new HashSet<ulong> { start };
        var parent = new Dictionary<ulong, ulong>();
        var queue = new Queue<ulong>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong current = queue.Dequeue();

            if (current == target)
                return BuildPath(heap, parent, start, target);

            ClrObject obj = heap.GetObject(current);
            if (!obj.IsValid)
                continue;

            foreach (ClrObject reference in obj.EnumerateReferences())
            {
                ulong next = reference.Address;
                if (next != 0 && visited.Add(next))
                {
                    parent[next] = current;
                    queue.Enqueue(next);
                }
            }
        }

        return null;
    }

    private static List<GcRootNode> BuildPath(ClrHeap heap, Dictionary<ulong, ulong> parent, ulong start, ulong target)
    {
        var addresses = new List<ulong>();
        ulong node = target;
        addresses.Add(node);
        while (node != start)
        {
            node = parent[node];
            addresses.Add(node);
        }
        addresses.Reverse();

        return addresses
            .Select(addr => new GcRootNode(addr, heap.GetObject(addr).Type?.Name ?? "<unknown>"))
            .ToList();
    }

    private static string DescribeRoot(ClrRoot root)
    {
        string type = root.Object.Type?.Name ?? "<unknown>";
        return $"{root.RootKind} @ {root.Address:x12} -> {type}";
    }
}
