using System;
using System.Collections.Generic;
using System.Threading;
using Sherlock.Core.HeapModel;

namespace Sherlock.Core.Analysis;

/// <summary>Computes and caches dominators over a persisted heap graph.</summary>
public sealed class DominatorAnalyzer(Snapshot snapshot)
{
    /// <summary>
    /// Dominator columns by graph node: object ids, then the synthetic root at <see cref="HeapGraph.Root"/>.
    /// <see cref="Idom"/> is each node's immediate dominator (the root's is itself, an unreachable object's is -1),
    /// and <see cref="Children"/> lists each node's dominated children from <see cref="ChildOffsets"/>, largest
    /// retained size first.
    /// </summary>
    public readonly record struct DominatorResult(ReadOnlyMemory<int> Idom, ReadOnlyMemory<ulong> Retained, ReadOnlyMemory<long> ChildOffsets, ReadOnlyMemory<int> Children);

    public DominatorTree Build(CancellationToken cancellationToken = default)
    {
        HeapGraph graph = snapshot.GetHeapGraph(cancellationToken);
        string path = System.IO.Path.Combine(snapshot.CacheDirectory, "dominators.slab");
        return TryLoad(path, graph) ?? ComputeAndPersist(graph, path, cancellationToken);
    }

    private static DominatorTree? TryLoad(string path, HeapGraph graph)
    {
        if (!System.IO.File.Exists(path))
        {
            return null;
        }
        try
        {
            return DominatorTreeStore.Load(path, graph);
        }
        catch
        {
            return null;
        }
    }

    // Reads the saved tree back, so the computed arrays can be released.
    private static DominatorTree ComputeAndPersist(HeapGraph graph, string path, CancellationToken cancellationToken)
    {
        DominatorResult r = Compute(graph, cancellationToken);
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            DominatorTreeStore.Save(path, r, graph.Id);
        }
        catch
        {
            // Analysis remains usable when the dump directory is read-only.
            return new DominatorTree(graph, r);
        }
        return TryLoad(path, graph) ?? new DominatorTree(graph, r);
    }

    /// <summary>Computes immediate dominators and retained sizes without ClrMD.</summary>
    public static DominatorResult Compute(HeapGraph graph, CancellationToken cancellationToken = default)
    {
        int root = graph.Root;
        int nodeCount = graph.NodeCount;

        int[] rpoNumber = BuildReversePostorder(graph, root, nodeCount, cancellationToken, out int[] nodeByRpo);
        int m = nodeByRpo.Length;

        // Reverse-CSR in RPO space: count predecessors, prefix-sum offsets, then scatter edges.
        var predOffsets = new int[m + 1];
        long reachableEdges = 0;
        for (int node = 0; node < nodeCount; node++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rpoNumber[node] < 0)
            {
                continue;
            }

            foreach (int v in graph.Successors(node))
            {
                int vRpo = rpoNumber[v];
                if (vRpo >= 0) { predOffsets[vRpo + 1]++; reachableEdges++; }
            }
        }
        // Predecessors occupy one int[]; reject overflow even though graph edges are long-indexed.
        if (reachableEdges > int.MaxValue)
        {
            throw new NotSupportedException(
                $"{reachableEdges:N0} reachable edges exceed the 2.1B dominator limit; chunked predecessor storage is not yet implemented.");
        }
        for (int i = 0; i < m; i++)
            predOffsets[i + 1] += predOffsets[i];
        var predEdges = new int[predOffsets[m]];
        var cursor = (int[])predOffsets.Clone();
        for (int node = 0; node < nodeCount; node++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int uRpo = rpoNumber[node];
            if (uRpo < 0)
            {
                continue;
            }

            foreach (int v in graph.Successors(node))
            {
                int vRpo = rpoNumber[v];
                if (vRpo >= 0)
                {
                    predEdges[cursor[vRpo]++] = uRpo;
                }
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
                for (int e = predOffsets[b]; e < predOffsets[b + 1]; e++)
                {
                    int p = predEdges[e];
                    if (idom[p] == -1)
                    {
                        continue;
                    }

                    newIdom = newIdom == -1 ? p : Intersect(p, newIdom, idom);
                }
                if (newIdom != -1 && idom[b] != newIdom) { idom[b] = newIdom; changed = true; }
            }
        }

        // Descendants have higher RPO, so reverse accumulation sums children before their dominator.
        ReadOnlySpan<uint> sizes = graph.Sizes.Span;
        var retainedByRpo = new ulong[m];
        for (int rpo = 1; rpo < m; rpo++)
        {
            int node = nodeByRpo[rpo];
            retainedByRpo[rpo] = sizes[node] != uint.MaxValue ? sizes[node] : graph.SizeOf(node);
        }
        for (int rpo = m - 1; rpo >= 1; rpo--)
            retainedByRpo[idom[rpo]] += retainedByRpo[rpo];

        return ByNode(nodeCount, nodeByRpo, idom, retainedByRpo, cancellationToken);
    }

    // Moves the columns from reverse postorder to node ids and lists each node's children, largest first.
    private static DominatorResult ByNode(int nodeCount, int[] nodeByRpo, int[] idomByRpo, ulong[] retainedByRpo, CancellationToken cancellationToken)
    {
        int m = nodeByRpo.Length;
        var idom = new int[nodeCount];
        var retained = new ulong[nodeCount];
        Array.Fill(idom, -1);
        var childOffsets = new long[nodeCount + 1];
        for (int rpo = 0; rpo < m; rpo++)
        {
            int node = nodeByRpo[rpo];
            idom[node] = nodeByRpo[idomByRpo[rpo]];
            retained[node] = retainedByRpo[rpo];
            if (rpo > 0)
            {
                childOffsets[idom[node] + 1]++;
            }
        }
        for (int node = 0; node < nodeCount; node++)
            childOffsets[node + 1] += childOffsets[node];

        var children = new int[m - 1];
        var cursor = (long[])childOffsets.Clone();
        for (int rpo = 1; rpo < m; rpo++)
        {
            int node = nodeByRpo[rpo];
            children[cursor[idom[node]]++] = node;
        }
        Comparison<int> largestFirst = (a, b) => retained[b] != retained[a] ? retained[b].CompareTo(retained[a]) : a.CompareTo(b);
        for (int node = 0; node < nodeCount; node++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<int> mine = children.AsSpan((int)childOffsets[node], (int)(childOffsets[node + 1] - childOffsets[node]));
            if (mine.Length > 1)
            {
                mine.Sort(largestFirst);
            }
        }
        return new DominatorResult(idom, retained, childOffsets, children);
    }

    private static int[] BuildReversePostorder(HeapGraph graph, int root, int nodeCount, CancellationToken cancellationToken, out int[] nodeByRpo)
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
            cancellationToken.ThrowIfCancellationRequested();
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
