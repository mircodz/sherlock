using System;
using System.Collections.Generic;
using System.Threading;
using Sherlock.Core.HeapModel;

namespace Sherlock.Core.Analysis;

/// <summary>
/// The V2 dominator-tree builder. Unlike <see cref="DominatorAnalyzer"/> (V1, which walks the heap
/// through ClrMD every time: DAC-bound, ~1-2M edges/s, nothing cached), V2 computes over a
/// <see cref="HeapGraph"/>, a compact object graph extracted once by bypassing the DAC (~4-5x faster)
/// and persisted beside the dump as a <c>.slab</c>, so reopening a snapshot skips extraction. The
/// dominator math (Cooper-Harvey-Kennedy + retained sizes) is identical and produces the same
/// <see cref="DominatorTree"/>, kept separate until we make it the default.
/// </summary>
public sealed class DominatorAnalyzer(Snapshot snapshot)
{
    /// <summary>Pure result of the dominator computation over a <see cref="HeapGraph"/>: per RPO-ordered
    /// node its address, own (shallow) size, retained size, and immediate dominator (in RPO space). No
    /// ClrMD, testable against a hand-built graph. Index 0 is the synthetic root.</summary>
    public readonly record struct DominatorResult(ulong[] Address, ulong[] Own, ulong[] Retained, int[] Idom, int[] NodeByRpo);

    public DominatorTree Build(CancellationToken cancellationToken = default)
    {
        HeapGraph graph = snapshot.GetHeapGraph(cancellationToken);

        // Derived cache: load from the sidecar (validated against the graph's content hash) if present,
        // else compute and persist so reopen skips the recompute.
        string path = SidecarPath(snapshot.DumpPath);
        DominatorResult r = TryLoad(path, graph)
            ?? ComputeAndPersist(graph, path, cancellationToken);

        var rpoOf = new Dictionary<ulong, int>(r.Address.Length);
        for (int rpo = 1; rpo < r.Address.Length; rpo++)
            rpoOf[r.Address[rpo]] = rpo;

        // If the graph carries a type column, resolve type names from it (no ClrMD) so the tree never
        // re-enters the DAC to name a node. Falls back to ClrMD when the graph has no types.
        Func<ulong, string>? typeNames = graph.HasTypes
            ? address => { int id = graph.IndexOf(address); return id >= 0 ? graph.TypeNameOf(id) ?? "<unknown>" : "<unknown>"; }
        : null;

        return new DominatorTree(snapshot.Runtime.Heap, r.Address, r.Own, r.Retained, r.Idom, rpoOf, typeNames);
    }

    /// <summary>The dominator-cache sidecar path for a dump: <c>&lt;dump&gt;.dominators.slab</c>.</summary>
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
            return null; // corrupt / partial sidecar, recompute
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
            // Best-effort persistence: a read-only dump directory just means we recompute next time.
        }
        return r;
    }

    /// <summary>Computes the dominator tree + retained sizes purely from the graph (Cooper-Harvey-Kennedy
    /// over reverse-postorder), with no dependency on ClrMD.</summary>
    public static DominatorResult Compute(HeapGraph graph, CancellationToken cancellationToken = default)
    {
        int root = graph.Root;
        int nodeCount = graph.NodeCount;

        // Reverse-postorder from the synthetic root (iterative DFS).
        int[] rpoNumber = BuildReversePostorder(graph, root, nodeCount, out int[] nodeByRpo);
        int m = nodeByRpo.Length;

        // Predecessor lists in RPO space as reverse-CSR (two flat arrays) built by counting sort: no
        // per-node List<int> (m allocations + pointer-chasing, the first thing to OOM on a 400M-edge
        // graph). Pass 1 counts in-degree, prefix-sum gives offsets, pass 2 scatters.
        var predOffsets = new int[m + 1];
        long reachableEdges = 0;
        for (int node = 0; node < nodeCount; node++)
        {
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
