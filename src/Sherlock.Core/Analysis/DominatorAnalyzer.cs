using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>
/// Builds a <see cref="DominatorTree"/> for the managed heap using the
/// Cooper-Harvey-Kennedy iterative dominators algorithm
/// (<i>A Simple, Fast Dominance Algorithm</i>, 2001).
/// </summary>
/// <remarks>
/// The whole object graph is held in memory while building, so cost is roughly
/// O(objects + references) in time and space. Fine for typical dumps; very large
/// heaps (tens of millions of objects) may need a streaming approach later.
/// </remarks>
public sealed class DominatorAnalyzer(DumpSession session)
{
    public DominatorTree Build(CancellationToken cancellationToken = default)
    {
        ClrHeap heap = session.Runtime.Heap;

        // 1. Index every (non-free) object: address -> dense id, plus shallow sizes.
        var indexOf = new Dictionary<ulong, int>();
        var addresses = new List<ulong>();
        var sizes = new List<ulong>();
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (obj.Type is null || obj.IsFree)
            {
                continue;
            }

            indexOf[obj.Address] = addresses.Count;
            addresses.Add(obj.Address);
            sizes.Add(obj.Size);
        }

        int objectCount = addresses.Count;
        int root = objectCount;            // synthetic root id
        int nodeCount = objectCount + 1;

        // 2. Successor edges (second pass; forward refs need the full index first).
        var successors = new List<int>?[nodeCount];
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (obj.Type is null || obj.IsFree || !indexOf.TryGetValue(obj.Address, out int u))
            {
                continue;
            }

            foreach (ClrObject reference in obj.EnumerateReferences())
            {
                if (indexOf.TryGetValue(reference.Address, out int v))
                {
                    (successors[u] ??= []).Add(v);
                }
            }
        }

        // Synthetic root points at every GC-rooted object.
        var rootTargets = new HashSet<int>();
        foreach (ClrRoot clrRoot in heap.EnumerateRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (indexOf.TryGetValue(clrRoot.Object.Address, out int v))
            {
                rootTargets.Add(v);
            }
        }
        successors[root] = rootTargets.ToList();

        // 3. Reverse-postorder numbering from the synthetic root (iterative DFS).
        int[] rpoNumber = BuildReversePostorder(successors, root, nodeCount, out int[] nodeByRpo);
        int m = nodeByRpo.Length;

        // 4. Predecessor lists in RPO space.
        var preds = new List<int>[m];
        for (int i = 0; i < m; i++)
            preds[i] = [];
        for (int node = 0; node < nodeCount; node++)
        {
            int uRpo = rpoNumber[node];
            if (uRpo < 0 || successors[node] is not { } succ)
            {
                continue;
            }

            foreach (int v in succ)
            {
                int vRpo = rpoNumber[v];
                if (vRpo >= 0)
                {
                    preds[vRpo].Add(uRpo);
                }
            }
        }

        // 5. Iterative dominators (CHK). idom indexed in RPO space; root dominates itself.
        var idom = new int[m];
        Array.Fill(idom, -1);
        idom[0] = 0;
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int b = 1; b < m; b++)
            {
                int newIdom = -1;
                foreach (int p in preds[b])
                {
                    if (idom[p] == -1)
                    {
                        continue;
                    }

                    newIdom = newIdom == -1 ? p : Intersect(p, newIdom, idom);
                }
                if (newIdom != -1 && idom[b] != newIdom)
                {
                    idom[b] = newIdom;
                    changed = true;
                }
            }
        }

        // 6. Retained sizes. own[k] for the synthetic root is 0; otherwise the
        //    object's shallow size. Walk RPO high->low, accumulating each node into
        //    its immediate dominator - descendants (higher RPO) are already summed.
        var address = new ulong[m];
        var own = new ulong[m];
        for (int rpo = 0; rpo < m; rpo++)
        {
            int node = nodeByRpo[rpo];
            if (node == root)
            {
                address[rpo] = 0;
                own[rpo] = 0;
            }
            else
            {
                address[rpo] = addresses[node];
                own[rpo] = sizes[node];
            }
        }

        var retained = (ulong[])own.Clone();
        for (int rpo = m - 1; rpo >= 1; rpo--)
            retained[idom[rpo]] += retained[rpo];

        var rpoOf = new Dictionary<ulong, int>(m);
        for (int rpo = 1; rpo < m; rpo++)
            rpoOf[address[rpo]] = rpo;

        return new DominatorTree(heap, address, own, retained, idom, rpoOf);
    }

    /// <summary>
    /// Assigns reverse-postorder numbers (root = 0) to nodes reachable from
    /// <paramref name="root"/>. Unreachable nodes get -1. Iterative to avoid deep recursion.
    /// </summary>
    private static int[] BuildReversePostorder(List<int>?[] successors, int root, int nodeCount, out int[] nodeByRpo)
    {
        var rpoNumber = new int[nodeCount];
        Array.Fill(rpoNumber, -1);

        var postorder = new List<int>();
        var visited = new bool[nodeCount];
        var stack = new Stack<(int Node, int Index)>();

        visited[root] = true;
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            (int node, int index) = stack.Pop();
            List<int>? succ = successors[node];
            if (succ is not null && index < succ.Count)
            {
                stack.Push((node, index + 1));
                int w = succ[index];
                if (!visited[w])
                {
                    visited[w] = true;
                    stack.Push((w, 0));
                }
            }
            else
            {
                postorder.Add(node);
            }
        }

        int m = postorder.Count;
        nodeByRpo = new int[m];
        for (int k = 0; k < m; k++)
        {
            int node = postorder[m - 1 - k];
            rpoNumber[node] = k;
            nodeByRpo[k] = node;
        }
        return rpoNumber;
    }

    /// <summary>Finds the nearest common dominator of two nodes in RPO space.</summary>
    private static int Intersect(int a, int b, int[] idom)
    {
        while (a != b)
        {
            while (a > b)
            {
                a = idom[a];
            }

            while (b > a)
            {
                b = idom[b];
            }
        }
        return a;
    }
}
