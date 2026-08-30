using System.Collections.Generic;
using System.Linq;
using Sherlock.Core.Analysis;
using Sherlock.Core.HeapModel;

namespace Sherlock.Core.Tests.HeapModel;

/// <summary>
/// Ground-truth tests for the dominator + retained-size computation, over hand-built graphs whose exact
/// dominators and retained sizes are known by construction. This pins ABSOLUTE correctness (not just
/// "matches ClrMD") of <see cref="DominatorAnalyzer.Compute"/>, the pure graph math behind the
/// biggest-memory-holders analysis.
/// </summary>
public sealed class DominatorGroundTruthTests
{
    // Builds a HeapGraph from an explicit edge list. Nodes are 0..n-1 with the given sizes; `roots` are
    // the GC roots (successors of the synthetic root node n). Successor lists are kept in input order.
    private static HeapGraph Build(uint[] sizes, int[] roots, params (int From, int To)[] edges)
    {
        int n = sizes.Length;
        var succ = new List<int>[n];
        for (int i = 0; i < n; i++) succ[i] = [];
        foreach ((int from, int to) in edges) succ[from].Add(to);

        // CSR: objects 0..n-1, then the synthetic root (n), then the terminator → Offsets length n+2.
        var offsets = new int[n + 2];
        var edgeList = new List<int>();
        for (int i = 0; i < n; i++)
        {
            offsets[i] = edgeList.Count;
            edgeList.AddRange(succ[i]);
        }
        offsets[n] = edgeList.Count;      // synthetic root's edges = the GC roots
        edgeList.AddRange(roots);
        offsets[n + 1] = edgeList.Count;

        // Addresses must be sorted + distinct; use a fixed stride so id == index and IndexOf works.
        var addresses = new ulong[n];
        for (int i = 0; i < n; i++) addresses[i] = 0x1000 + (ulong)i * 0x100;

        return new HeapGraph(addresses, sizes, offsets, edgeList.ToArray());
    }

    // Convenience: retained size of object id `obj` from a computed result (results are RPO-indexed).
    private static ulong RetainedOf(DominatorAnalyzer.DominatorResult r, HeapGraph g, int obj)
    {
        ulong addr = g.Addresses.Span[obj];
        for (int rpo = 1; rpo < r.Address.Length; rpo++)
            if (r.Address[rpo] == addr)
            {
                return r.Retained[rpo];
            }

        return 0;
    }

    // The RPO index of object id `obj` in a computed result (results are RPO-indexed), or -1.
    private static int FindRpo(DominatorAnalyzer.DominatorResult r, HeapGraph g, int obj)
    {
        ulong addr = g.Addresses.Span[obj];
        for (int rpo = 1; rpo < r.Address.Length; rpo++)
            if (r.Address[rpo] == addr)
            {
                return rpo;
            }

        return -1;
    }

    // The immediate dominator of object `obj` as an object id, or -1 if it's the synthetic root.
    private static int IdomObjectOf(DominatorAnalyzer.DominatorResult r, HeapGraph g, int obj)
    {
        int domNode = r.NodeByRpo[r.Idom[FindRpo(r, g, obj)]];
        return domNode == g.Root ? -1 : domNode;
    }

    [Fact]
    public void Chain_RetainedIsSuffixSum()
    {
        // root -> 0 -> 1 -> 2 -> 3 (a linked list). Each node retains itself + everything below it.
        var g = Build(sizes: [10, 20, 30, 40], roots: [0],
            (0, 1), (1, 2), (2, 3));
        var r = DominatorAnalyzer.Compute(g);

        Assert.Equal(100u, RetainedOf(r, g, 0)); // 10+20+30+40
        Assert.Equal(90u, RetainedOf(r, g, 1));  // 20+30+40
        Assert.Equal(70u, RetainedOf(r, g, 2));  // 30+40
        Assert.Equal(40u, RetainedOf(r, g, 3));  // 40
        Assert.Equal(100u, r.Retained[0]);       // total reachable (the synthetic root)
    }

    [Fact]
    public void Diamond_SharedChildDominatedByJoin()
    {
        // root -> A(0); A -> B(1), C(2); B -> D(3); C -> D(3).
        // Both paths to D pass through A, so A dominates D: D's bytes retained by A, NOT by B or C alone.
        var g = Build(sizes: [10, 20, 30, 40], roots: [0],
            (0, 1), (0, 2), (1, 3), (2, 3));
        var r = DominatorAnalyzer.Compute(g);

        Assert.Equal(100u, RetainedOf(r, g, 0)); // A retains A+B+C+D = 10+20+30+40
        Assert.Equal(20u, RetainedOf(r, g, 1));  // B retains only itself (D not dominated by B)
        Assert.Equal(30u, RetainedOf(r, g, 2));  // C retains only itself
        Assert.Equal(40u, RetainedOf(r, g, 3));  // D retains itself
        Assert.Equal(0, IdomObjectOf(r, g, 3));  // idom(D) == A (the join node), not B or C
    }

    [Fact]
    public void Diamond_IdomOfSharedChildIsTheJoinNode()
    {
        // Same diamond; verify idom(D) == A (object 0), not B or C.
        var g = Build(sizes: [10, 20, 30, 40], roots: [0],
            (0, 1), (0, 2), (1, 3), (2, 3));
        var r = DominatorAnalyzer.Compute(g);

        Assert.Equal(0, IdomObjectOf(r, g, 3)); // A dominates D
    }

    [Fact]
    public void TwoRoots_SharedObjectDominatedBySyntheticRoot()
    {
        // root -> A(0), root -> B(1); A -> S(2), B -> S(2).
        // S is reachable from two independent roots, so its only dominator is the synthetic root:
        // neither A nor B retains S; it counts only toward total reachable.
        var g = Build(sizes: [10, 20, 30], roots: [0, 1],
            (0, 2), (1, 2));
        var r = DominatorAnalyzer.Compute(g);

        Assert.Equal(10u, RetainedOf(r, g, 0)); // A retains only itself (not S)
        Assert.Equal(20u, RetainedOf(r, g, 1)); // B retains only itself
        Assert.Equal(30u, RetainedOf(r, g, 2)); // S retains itself
        Assert.Equal(60u, r.Retained[0]);       // total reachable = 10+20+30

        int sRpo = FindRpo(r, g, 2);
        Assert.Equal(g.Root, r.NodeByRpo[r.Idom[sRpo]]); // idom(S) == synthetic root
    }

    [Fact]
    public void Cycle_HandledWithoutInfiniteLoopAndCorrectRetained()
    {
        // root -> A(0); A -> B(1); B -> A(0) (a cycle). Both retained by A via the root path.
        var g = Build(sizes: [10, 20], roots: [0],
            (0, 1), (1, 0));
        var r = DominatorAnalyzer.Compute(g);

        Assert.Equal(30u, RetainedOf(r, g, 0)); // A retains A+B
        Assert.Equal(20u, RetainedOf(r, g, 1)); // B retains itself
        Assert.Equal(30u, r.Retained[0]);
    }

    [Fact]
    public void UnreachableObject_NotCountedInReachableTotal()
    {
        // root -> A(0); B(1) is allocated but unreachable from any root.
        var g = Build(sizes: [10, 20], roots: [0]);
        var r = DominatorAnalyzer.Compute(g);

        Assert.Equal(10u, r.Retained[0]); // only A is reachable; B excluded from the total
        // B has no RPO number, so it never appears in the result address array.
        Assert.DoesNotContain(g.Addresses.Span[1], r.Address.Skip(1));
    }
}
