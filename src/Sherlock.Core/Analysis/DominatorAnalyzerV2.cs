using System;
using System.Collections.Generic;
using System.Threading;
using Sherlock.Core.HeapModel;

namespace Sherlock.Core.Analysis;

/// <summary>
/// The V2 dominator-tree builder. It differs from <see cref="DominatorAnalyzer"/> only in <i>where the
/// graph comes from</i>: V1 walks the heap through ClrMD every time (DAC-bound, ~1-2M edges/s, nothing
/// cached); V2 computes over a <see cref="HeapGraph"/> — a compact object graph extracted once by
/// bypassing the DAC (~4-5x faster) and persisted beside the dump as a <c>.slab</c>, so reopening a
/// snapshot skips extraction entirely. The dominator math (Cooper-Harvey-Kennedy + retained sizes) is
/// identical and produces the same <see cref="DominatorTree"/>, so this is a drop-in alternative kept
/// separate until we're ready to make it the default.
/// </summary>
public sealed class DominatorAnalyzerV2(DumpSession session)
{
    public DominatorTree Build(CancellationToken cancellationToken = default)
    {
        HeapGraph graph = session.GetHeapGraph(cancellationToken);

        int root = graph.Root;
        int nodeCount = graph.NodeCount;

        // Reverse-postorder from the synthetic root (iterative DFS).
        int[] rpoNumber = BuildReversePostorder(graph, root, nodeCount, out int[] nodeByRpo);
        int m = nodeByRpo.Length;

        // Predecessor lists in RPO space.
        var preds = new List<int>[m];
        for (int i = 0; i < m; i++)
            preds[i] = [];
        for (int node = 0; node < nodeCount; node++)
        {
            int uRpo = rpoNumber[node];
            if (uRpo < 0) continue;
            foreach (int v in graph.Successors(node))
            {
                int vRpo = rpoNumber[v];
                if (vRpo >= 0) preds[vRpo].Add(uRpo);
            }
        }

        // Iterative dominators (CHK). idom in RPO space; root dominates itself.
        var idom = new int[m];
        Array.Fill(idom, -1);
        idom[0] = 0;
        bool changed = true;
        while (changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changed = false;
            for (int b = 1; b < m; b++)
            {
                int newIdom = -1;
                foreach (int p in preds[b])
                {
                    if (idom[p] == -1) continue;
                    newIdom = newIdom == -1 ? p : Intersect(p, newIdom, idom);
                }
                if (newIdom != -1 && idom[b] != newIdom) { idom[b] = newIdom; changed = true; }
            }
        }

        // Retained sizes, in RPO space (accumulate each node into its immediate dominator; descendants,
        // at higher RPO, are already summed).
        var address = new ulong[m];
        var own = new ulong[m];
        for (int rpo = 0; rpo < m; rpo++)
        {
            int node = nodeByRpo[rpo];
            if (node != root)
            {
                address[rpo] = graph.Addresses[node];
                own[rpo] = graph.Sizes[node];
            }
        }

        var retained = (ulong[])own.Clone();
        for (int rpo = m - 1; rpo >= 1; rpo--)
            retained[idom[rpo]] += retained[rpo];

        var rpoOf = new Dictionary<ulong, int>(m);
        for (int rpo = 1; rpo < m; rpo++)
            rpoOf[address[rpo]] = rpo;

        return new DominatorTree(session.Runtime.Heap, address, own, retained, idom, rpoOf);
    }

    private static int[] BuildReversePostorder(HeapGraph graph, int root, int nodeCount, out int[] nodeByRpo)
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
            ReadOnlySpan<int> succ = graph.Successors(node);
            if (index < succ.Length)
            {
                stack.Push((node, index + 1));
                int w = succ[index];
                if (!visited[w]) { visited[w] = true; stack.Push((w, 0)); }
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

    private static int Intersect(int a, int b, int[] idom)
    {
        while (a != b)
        {
            while (a > b) a = idom[a];
            while (b > a) b = idom[b];
        }
        return a;
    }
}
