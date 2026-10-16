using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.Analysis;
using Sherlock.Core.HeapModel;
using Xunit;

namespace Sherlock.Core.Tests.Analysis;

public sealed class RetentionPathTests
{
    // Nodes are 0..n-1 with names T{id}; node n is the synthetic GC root.
    private static HeapGraph Build(uint[] sizes, int[] roots, params (int From, int To)[] edges)
    {
        int n = sizes.Length;
        var succ = new List<int>[n];
        for (int i = 0; i < n; i++) succ[i] = [];
        foreach ((int from, int to) in edges) succ[from].Add(to);

        var offsets = new int[n + 2];
        var edgeList = new List<int>();
        for (int i = 0; i < n; i++) { offsets[i] = edgeList.Count; edgeList.AddRange(succ[i]); }
        offsets[n] = edgeList.Count;
        edgeList.AddRange(roots);
        offsets[n + 1] = edgeList.Count;

        var addresses = new ulong[n];
        for (int i = 0; i < n; i++) addresses[i] = 0x1000 + (ulong)i * 0x100;

        var typeIds = new int[n];
        for (int i = 0; i < n; i++) typeIds[i] = i;
        var typeNames = Enumerable.Range(0, n).Select(i => $"T{i}").ToArray();

        return new HeapGraph(addresses, sizes, offsets, edgeList.ToArray(), typeIds, typeNames);
    }

    // Resolve names from the graph so the null ClrHeap is never dereferenced.
    private static DominatorTree TreeOf(HeapGraph g)
    {
        DominatorAnalyzer.DominatorResult r = DominatorAnalyzer.Compute(g);
        var rpoOf = new Dictionary<ulong, int>(r.Address.Length);
        for (int rpo = 1; rpo < r.Address.Length; rpo++) rpoOf[r.Address[rpo]] = rpo;
        string Resolve(ulong addr) { int id = g.IndexOf(addr); return id >= 0 ? g.TypeNameOf(id) : "?"; }
        return new DominatorTree(r.Address, r.Own, r.Retained, r.Idom, rpoOf, Resolve);
    }

    private static string[] PathTypes(DominatorTree t, HeapGraph g, int obj) =>
        (t.RetentionPath(g.Addresses.Span[obj]) ?? []).Select(n => n.TypeName).ToArray();

    [Fact]
    public void Chain_PathIsRootToTargetInclusive()
    {
        HeapGraph g = Build([10, 10, 10], roots: [0], (0, 1), (1, 2));
        DominatorTree t = TreeOf(g);

        Assert.Equal(["T0", "T1", "T2"], PathTypes(t, g, 2));
        Assert.Equal(["T0", "T1"], PathTypes(t, g, 1));
        Assert.Equal(["T0"], PathTypes(t, g, 0));
    }

    [Fact]
    public void Diamond_PathStopsAtDominatorNotEveryHolder()
    {
        // Every root path to 3 passes through 0, but neither holder 1 nor 2 dominates it.
        HeapGraph g = Build([10, 10, 10, 10], roots: [0], (0, 1), (0, 2), (1, 3), (2, 3));
        DominatorTree t = TreeOf(g);

        Assert.Equal(["T0", "T3"], PathTypes(t, g, 3));
    }

    [Fact]
    public void RootAnalyzerReturnsRealReferenceEdges()
    {
        HeapGraph g = Build([10, 10, 10, 10], roots: [0], (0, 1), (0, 2), (1, 3), (2, 3));
        GcRootPath path = Assert.Single(RootAnalyzer.Find(g, g.Addresses.Span[3]));

        Assert.Contains(g.IndexOf(path.Path[0].Address), g.Successors(g.Root).ToArray());
        for (int i = 0; i + 1 < path.Path.Count; i++)
        {
            Assert.Contains(g.IndexOf(path.Path[i + 1].Address), g.Successors(g.IndexOf(path.Path[i].Address)).ToArray());
        }
    }

    [Fact]
    public void RootAnalyzerReturnsNoPathForUnreachableObjects()
    {
        HeapGraph g = Build([10, 10], roots: [0]);
        Assert.Empty(RootAnalyzer.Find(g, g.Addresses.Span[1]));
    }

    [Fact]
    public void RootAnalyzerReturnsEveryRoot()
    {
        HeapGraph g = Build([10, 10, 10], roots: [0, 1, 1], (0, 2), (1, 2));
        IReadOnlyList<GcRootPath> paths = RootAnalyzer.Find(g, g.Addresses.Span[2]);

        Assert.Equal(3, paths.Count);
        Assert.Equal([g.Addresses.Span[0], g.Addresses.Span[1], g.Addresses.Span[1]], paths.Select(path => path.Path[0].Address));
    }

    [Fact]
    public void RootAnalyzerPreservesClrRootMetadata()
    {
        HeapGraph graph = Build([10, 10], roots: [0], (0, 1));
        HeapRootRecord[] roots = [new(0, 0x1234, ClrRootKind.PinnedHandle, true, true)];
        var withMetadata = new HeapGraph(Guid.NewGuid(), graph.Addresses, graph.Sizes, default, graph.Offsets, graph.Edges,
            graph.TypeIds, graph.TypeNames, 0, 0, roots);
        GcRootInfo root = Assert.Single(RootAnalyzer.Find(withMetadata, graph.Addresses.Span[1])).Root;

        Assert.Equal(0x1234UL, root.Address);
        Assert.Equal("PinnedHandle", root.Kind);
        Assert.True(root.IsInterior);
        Assert.True(root.IsPinned);
    }

    [Fact]
    public void RootAnalyzerHandlesCycles()
    {
        HeapGraph g = Build([10, 10, 10], roots: [0], (0, 1), (1, 0), (1, 2));
        Assert.Equal(["T0", "T1", "T2"], Assert.Single(RootAnalyzer.Find(g, g.Addresses.Span[2])).Path.Select(node => node.TypeName));
    }

    [Fact]
    public void RootAnalyzerUsesClrMdReferenceOrder()
    {
        HeapGraph g = Build([10, 10, 10, 10], roots: [0], (0, 2), (0, 1), (1, 3), (2, 3));
        Assert.Equal(["T0", "T2", "T3"], Assert.Single(RootAnalyzer.Find(g, g.Addresses.Span[3])).Path.Select(node => node.TypeName));
    }

    [Fact]
    public void Unreachable_ReturnsNull()
    {
        HeapGraph g = Build([10, 10], roots: [0]);
        DominatorTree t = TreeOf(g);

        Assert.Null(t.RetentionPath(g.Addresses.Span[1]));
    }

    // A search from one root used to leave nodes it queued but never explored marked as seen, so a later root couldn't
    // pass through them: here 4 reaches 1 through 2 and 0, which the search from 3 had queued.
    [Fact]
    public void RootAnalyzerFindsRootsThroughObjectsAnEarlierRootReached()
    {
        HeapGraph g = Build([10, 10, 10, 10, 10], roots: [3, 4], (3, 4), (3, 0), (4, 2), (2, 0), (0, 1));
        IReadOnlyList<GcRootPath> paths = RootAnalyzer.Find(g, g.Addresses.Span[1]);

        Assert.Equal([g.Addresses.Span[3], g.Addresses.Span[4]], paths.Select(path => path.Path[0].Address));
        Assert.Equal(["T3", "T0", "T1"], paths[0].Path.Select(node => node.TypeName));
        Assert.Equal(["T4", "T2", "T0", "T1"], paths[1].Path.Select(node => node.TypeName));
    }

    [Fact]
    public void RootAnalyzerAgreesWithBruteForceOnRandomGraphs()
    {
        var random = new System.Random(20261016);
        for (int round = 0; round < 3000; round++)
        {
            int n = random.Next(2, 12);
            var edges = new List<(int, int)>();
            for (int from = 0; from < n; from++)
            {
                for (int to = 0; to < n; to++)
                {
                    if (random.NextDouble() < 0.2) edges.Add((from, to));
                }
            }
            int[] roots = Enumerable.Range(0, random.Next(1, 4)).Select(_ => random.Next(n)).ToArray();
            HeapGraph g = Build(Enumerable.Repeat(10u, n).ToArray(), roots, [.. edges]);
            int target = random.Next(n);

            IReadOnlyList<GcRootPath> found = RootAnalyzer.Find(g, g.Addresses.Span[target]);

            int[] expectedRoots = roots.Where(root => Distance(g, root, target) >= 0).ToArray();
            Assert.Equal(expectedRoots.Select(root => g.Addresses.Span[root]), found.Select(path => path.Path[0].Address));
            foreach (GcRootPath path in found)
            {
                int[] ids = path.Path.Select(node => g.IndexOf(node.Address)).ToArray();
                Assert.Equal(target, ids[^1]);
                for (int i = 0; i + 1 < ids.Length; i++)
                {
                    Assert.Contains(ids[i + 1], g.Successors(ids[i]).ToArray());
                }
                Assert.Equal(Distance(g, ids[0], target), ids.Length - 1); // a shortest path
            }
        }
    }

    // Fewest references from one object to another, or -1.
    private static int Distance(HeapGraph g, int from, int to)
    {
        var steps = new Dictionary<int, int> { [from] = 0 };
        var queue = new Queue<int>([from]);
        while (queue.TryDequeue(out int node))
        {
            if (node == to) return steps[node];
            foreach (int next in g.Successors(node))
            {
                if (steps.TryAdd(next, steps[node] + 1)) queue.Enqueue(next);
            }
        }
        return -1;
    }
}
