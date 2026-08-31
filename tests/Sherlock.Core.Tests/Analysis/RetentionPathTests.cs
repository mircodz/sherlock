using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.Analysis;
using Sherlock.Core.HeapModel;
using Xunit;

namespace Sherlock.Core.Tests.Analysis;

/// <summary>
/// Ground-truth tests for dominator and real root paths over hand-built graphs whose chains are known by
/// construction. No ClrMD: the tree is built from a computed <see cref="DominatorAnalyzer.DominatorResult"/>
/// with a graph-backed type-name resolver, so the heap reference is never touched.
/// </summary>
public sealed class RetentionPathTests
{
    // Builds a HeapGraph from an explicit edge list. Nodes 0..n-1 with the given sizes; `roots` are the
    // GC roots (successors of the synthetic root n). Type names are "T{id}" so paths are checkable.
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

    // Constructs a graph-backed DominatorTree without opening a dump.
    private static DominatorTree TreeOf(HeapGraph g)
    {
        DominatorAnalyzer.DominatorResult r = DominatorAnalyzer.Compute(g);
        var rpoOf = new Dictionary<ulong, int>(r.Address.Length);
        for (int rpo = 1; rpo < r.Address.Length; rpo++) rpoOf[r.Address[rpo]] = rpo;
        string Resolve(ulong addr) { int id = g.IndexOf(addr); return id >= 0 ? g.TypeNameOf(id) ?? "?" : "?"; }
        return new DominatorTree(null!, r.Address, r.Own, r.Retained, r.Idom, rpoOf, Resolve);
    }

    private static string[] PathTypes(DominatorTree t, HeapGraph g, int obj) =>
        (t.RetentionPath(g.Addresses.Span[obj]) ?? []).Select(n => n.TypeName).ToArray();

    [Fact]
    public void Chain_PathIsRootToTargetInclusive()
    {
        // root -> 0 -> 1 -> 2. Retention path of 2 is [T0, T1, T2] (root-most first, target last).
        HeapGraph g = Build([10, 10, 10], roots: [0], (0, 1), (1, 2));
        DominatorTree t = TreeOf(g);

        Assert.Equal(["T0", "T1", "T2"], PathTypes(t, g, 2));
        Assert.Equal(["T0", "T1"], PathTypes(t, g, 1));
        Assert.Equal(["T0"], PathTypes(t, g, 0));
    }

    [Fact]
    public void Diamond_PathStopsAtDominatorNotEveryHolder()
    {
        // root -> 0; 0 -> 1; 0 -> 2; 1 -> 3; 2 -> 3. Node 3 has two holders (1 and 2), but its dominator
        // is 0 (every root path to 3 goes through 0), so the retention path is [T0, T3] — not T1/T2.
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
        var withMetadata = new HeapGraph(graph.Addresses, graph.Sizes, graph.Offsets, graph.Edges,
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
        // Node 1 is not reachable from the root (no edge into it) → collectable → null path.
        HeapGraph g = Build([10, 10], roots: [0]);
        DominatorTree t = TreeOf(g);

        Assert.Null(t.RetentionPath(g.Addresses.Span[1]));
    }

}
