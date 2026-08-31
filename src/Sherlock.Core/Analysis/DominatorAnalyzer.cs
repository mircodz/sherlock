using System;
using System.Collections.Generic;
using System.Threading;
using Sherlock.Core.HeapModel;

namespace Sherlock.Core.Analysis;

/// <summary>Computes and caches dominators over a persisted heap graph.</summary>
public sealed class DominatorAnalyzer(Snapshot snapshot)
{
    /// <summary>RPO-indexed dominator columns. Index 0 is the synthetic root.</summary>
    public readonly record struct DominatorResult(ulong[] Address, ulong[] Own, ulong[] Retained, int[] Idom, int[] NodeByRpo);

    public DominatorTree Build(CancellationToken cancellationToken = default)
    {
        HeapGraph graph = snapshot.GetHeapGraph(cancellationToken);

        string path = SidecarPath(snapshot.DumpPath);
        DominatorResult r = TryLoad(path, graph)
            ?? ComputeAndPersist(graph, path, cancellationToken);

        var rpoOf = new Dictionary<ulong, int>(r.Address.Length);
        for (int rpo = 1; rpo < r.Address.Length; rpo++)
            rpoOf[r.Address[rpo]] = rpo;

        Func<ulong, string>? typeNames = graph.HasTypes
            ? address => { int id = graph.IndexOf(address); return id >= 0 ? graph.TypeNameOf(id) ?? "<unknown>" : "<unknown>"; }
        : null;

        return new DominatorTree(snapshot.Runtime.Heap, r.Address, r.Own, r.Retained, r.Idom, rpoOf, typeNames);
    }

    public static string SidecarPath(string dumpPath) => System.IO.Path.GetFileName(dumpPath) == "heap.dmp" ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dumpPath)!, "dominators.slab") : dumpPath + ".dominators.slab";

    private static DominatorResult? TryLoad(string path, HeapGraph graph)
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

    private static DominatorResult ComputeAndPersist(HeapGraph graph, string path, CancellationToken cancellationToken)
    {
        DominatorResult r = Compute(graph, cancellationToken);
        try
        {
            DominatorTreeStore.Save(path, r, graph.ContentHash);
        }
        catch
        {
            // Analysis remains usable when the dump directory is read-only.
        }
        return r;
    }

    /// <summary>Computes immediate dominators and retained sizes without ClrMD.</summary>
    public static DominatorResult Compute(HeapGraph graph, CancellationToken cancellationToken = default)
    {
        int root = graph.Root;
        int nodeCount = graph.NodeCount;

        // Reverse-postorder from the synthetic root (iterative DFS).
        int[] rpoNumber = BuildReversePostorder(graph, root, nodeCount, cancellationToken, out int[] nodeByRpo);
        int m = nodeByRpo.Length;

        // Predecessor lists in RPO space as reverse-CSR (two flat arrays) built by counting sort: no
        // per-node List<int> (m allocations + pointer-chasing, the first thing to OOM on a 400M-edge
        // graph). Pass 1 counts in-degree, prefix-sum gives offsets, pass 2 scatters.
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
        // The reverse-CSR edge array is a single int[] (reachable-edge count long, indexed by int). A
        // reachable set with >2.1B refs is far beyond any 30 GB dump; fail loudly rather than corrupt
        // via overflow. Lifting this needs a chunked predecessor array (the next tier).
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

        // Retained sizes in RPO space: accumulate each node into its immediate dominator; descendants,
        // at higher RPO, are already summed.
        var address = new ulong[m];
        var own = new ulong[m];
        ReadOnlySpan<ulong> gAddr = graph.Addresses.Span;
        ReadOnlySpan<uint> gSize = graph.Sizes.Span;
        for (int rpo = 0; rpo < m; rpo++)
        {
            int node = nodeByRpo[rpo];
            if (node != root)
            {
                address[rpo] = gAddr[node];
                own[rpo] = gSize[node];
            }
        }

        var retained = (ulong[])own.Clone();
        for (int rpo = m - 1; rpo >= 1; rpo--)
            retained[idom[rpo]] += retained[rpo];

        return new DominatorResult(address, own, retained, idom, nodeByRpo);
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
