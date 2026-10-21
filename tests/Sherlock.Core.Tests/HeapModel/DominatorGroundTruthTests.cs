using System.Collections.Generic;
using System.Linq;
using Sherlock.Core.Tests.Common;
using Sherlock.Heap;
using Sherlock.Heap.Analysis;
using Xunit;

namespace Sherlock.Core.Tests.HeapModel;

public sealed class DominatorGroundTruthTests
{
    // Nodes are 0..n-1; node n is the synthetic root. Preserve successor input order.
    private static HeapGraph Build(uint[] sizes, int[] roots, params (int From, int To)[] edges)
    {
        int n = sizes.Length;
        var succ = new List<int>[n];
        for (int i = 0; i < n; i++) succ[i] = [];
        foreach ((int from, int to) in edges) succ[from].Add(to);

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

        // A fixed stride keeps addresses sorted and distinct.
        var addresses = new ulong[n];
        for (int i = 0; i < n; i++) addresses[i] = 0x1000 + (ulong)i * 0x100;

        return new HeapGraph(addresses, sizes, offsets, edgeList.ToArray());
    }

    private static ulong RetainedOf(DominatorAnalyzer.DominatorResult r, HeapGraph g, int obj) => r.Retained.Span[obj];

    // The immediate dominator of object `obj` as an object id, or -1 if it's the synthetic root.
    private static int IdomObjectOf(DominatorAnalyzer.DominatorResult r, HeapGraph g, int obj)
    {
        int dominator = r.Idom.Span[obj];
        return dominator == g.Root ? -1 : dominator;
    }

    [Fact]
    public void Chain_RetainedIsSuffixSum()
    {
        // root -> 0 -> 1 -> 2 -> 3 (a linked list). Each node retains itself + everything below it.
        var g = Build(sizes: [10, 20, 30, 40], roots: [0],
            (0, 1), (1, 2), (2, 3));
        using DominatorTree tree = GraphAnalysis.Dominators(g);
        var r = tree.Result;

        Assert.Equal(100u, RetainedOf(r, g, 0)); // 10+20+30+40
        Assert.Equal(90u, RetainedOf(r, g, 1));  // 20+30+40
        Assert.Equal(70u, RetainedOf(r, g, 2));  // 30+40
        Assert.Equal(40u, RetainedOf(r, g, 3));  // 40
        Assert.Equal(100u, r.Retained.Span[g.Root]); // total reachable (the synthetic root)
    }

    [Fact]
    public void Diamond_SharedChildDominatedByJoin()
    {
        // root -> A(0); A -> B(1), C(2); B -> D(3); C -> D(3).
        // Both paths to D pass through A, so A dominates D: D's bytes retained by A, NOT by B or C alone.
        var g = Build(sizes: [10, 20, 30, 40], roots: [0],
            (0, 1), (0, 2), (1, 3), (2, 3));
        using DominatorTree tree = GraphAnalysis.Dominators(g);
        var r = tree.Result;

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
        using DominatorTree tree = GraphAnalysis.Dominators(g);
        var r = tree.Result;

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
        using DominatorTree tree = GraphAnalysis.Dominators(g);
        var r = tree.Result;

        Assert.Equal(10u, RetainedOf(r, g, 0)); // A retains only itself (not S)
        Assert.Equal(20u, RetainedOf(r, g, 1)); // B retains only itself
        Assert.Equal(30u, RetainedOf(r, g, 2)); // S retains itself
        Assert.Equal(60u, r.Retained.Span[g.Root]); // total reachable = 10+20+30

        Assert.Equal(-1, IdomObjectOf(r, g, 2)); // idom(S) == synthetic root
    }

    [Fact]
    public void Cycle_HandledWithoutInfiniteLoopAndCorrectRetained()
    {
        // root -> A(0); A -> B(1); B -> A(0) (a cycle). Both retained by A via the root path.
        var g = Build(sizes: [10, 20], roots: [0],
            (0, 1), (1, 0));
        using DominatorTree tree = GraphAnalysis.Dominators(g);
        var r = tree.Result;

        Assert.Equal(30u, RetainedOf(r, g, 0)); // A retains A+B
        Assert.Equal(20u, RetainedOf(r, g, 1)); // B retains itself
        Assert.Equal(30u, r.Retained.Span[g.Root]);
    }

    [Fact]
    public void UnreachableObject_NotCountedInReachableTotal()
    {
        // root -> A(0); B(1) is allocated but unreachable from any root.
        var g = Build(sizes: [10, 20], roots: [0]);
        using DominatorTree tree = GraphAnalysis.Dominators(g);
        var r = tree.Result;

        Assert.Equal(10u, r.Retained.Span[g.Root]); // only A is reachable; B excluded from the total
        Assert.Equal(-1, r.Idom.Span[1]);            // B has no dominator
        Assert.DoesNotContain(1, r.Children.ToArray());
    }

    [Fact]
    public void ChildrenAreListedLargestFirst()
    {
        // root -> A(0); A -> B(1), C(2), D(3), each retaining only itself.
        var g = Build(sizes: [10, 20, 40, 30], roots: [0], (0, 1), (0, 2), (0, 3));
        using DominatorTree tree = GraphAnalysis.Dominators(g);

        Assert.Equal([40UL, 30UL, 20UL], tree.ImmediateChildren(g.Addresses.Span[0], 10).Select(n => n.RetainedSize));
        Assert.Equal([40UL, 30UL], tree.ImmediateChildren(g.Addresses.Span[0], 2).Select(n => n.RetainedSize));
    }

    [Fact]
    public void TopDominatorsMatchSortingEveryObjectOnRandomGraphs()
    {
        var random = new System.Random(7);
        for (int round = 0; round < 200; round++)
        {
            int n = random.Next(1, 40);
            uint[] sizes = Enumerable.Range(0, n).Select(_ => (uint)random.Next(1, 100)).ToArray();
            int[] roots = Enumerable.Range(0, random.Next(1, 4)).Select(_ => random.Next(n)).Distinct().ToArray();
            var edges = Enumerable.Range(0, random.Next(0, n * 3)).Select(_ => (random.Next(n), random.Next(n))).ToArray();
            var g = Build(sizes, roots, edges);
            using DominatorTree tree = GraphAnalysis.Dominators(g);
            var r = tree.Result;

            ulong[] expected = Enumerable.Range(0, n).Where(i => r.Idom.Span[i] >= 0)
                .Select(i => r.Retained.Span[i]).OrderByDescending(v => v).ToArray();
            Assert.Equal(expected, tree.TopDominators(n).Select(node => node.RetainedSize));
            Assert.Equal(expected.Length, tree.ObjectCount);
            foreach (DominatorGraphNode node in tree.BuildGraph(n).Nodes)
            {
                int dominator = r.Idom.Span[node.Id];
                Assert.Equal(dominator == g.Root ? null : dominator, node.ParentId);
            }
        }
    }

    // Packs ten random graphs with cycles, self-references, repeated references and shared objects into one, and checks
    // every immediate dominator and retained size against the definition: x dominates y when removing x leaves y
    // unreachable from the root.
    [Fact]
    public void AgreesWithTheDefinitionOnRandomGraphs()
    {
        var random = new System.Random(1906);
        for (int round = 0; round < 60; round++)
        {
            var sizes = new List<uint>();
            var roots = new List<int>();
            var edges = new List<(int, int)>();
            for (int part = 0; part < 10; part++)
            {
                int first = sizes.Count, n = random.Next(1, 16);
                sizes.AddRange(Enumerable.Range(0, n).Select(_ => (uint)random.Next(1, 1000)));
                roots.AddRange(Enumerable.Range(0, random.Next(0, 3)).Select(_ => first + random.Next(n)).Distinct());
                edges.AddRange(Enumerable.Range(0, random.Next(0, n * 3)).Select(_ => (first + random.Next(n), first + random.Next(n))));
            }
            HeapGraph g = Build([.. sizes], [.. roots], [.. edges]);
            using DominatorTree tree = GraphAnalysis.Dominators(g);
            var r = tree.Result;

            HashSet<int> reachable = ReachableAvoiding(g, -1);
            var dominators = new Dictionary<int, HashSet<int>>(); // each reachable object's strict dominators, root included
            foreach (int y in reachable.Where(y => y != g.Root))
            {
                dominators[y] = [.. reachable.Where(x => x != y && x != g.Root && !ReachableAvoiding(g, x).Contains(y)).Append(g.Root)];
            }
            for (int y = 0; y < g.ObjectCount; y++)
            {
                if (!reachable.Contains(y))
                {
                    Assert.Equal(-1, r.Idom.Span[y]);
                    Assert.Equal(0UL, r.Retained.Span[y]);
                    continue;
                }
                int idom = dominators[y].Single(d => dominators[y].SetEquals(d == g.Root ? [g.Root] : dominators[d].Append(d)));
                Assert.Equal(idom, r.Idom.Span[y]);
                ulong retained = (ulong)reachable.Where(z => z == y || (z != g.Root && dominators[z].Contains(y))).Sum(z => (long)sizes[z]);
                Assert.Equal(retained, r.Retained.Span[y]);
            }
            Assert.Equal(g.Root, r.Idom.Span[g.Root]);
            Assert.Equal((ulong)reachable.Where(z => z != g.Root).Sum(z => (long)sizes[z]), r.Retained.Span[g.Root]);
        }
    }

    // The nodes reachable from the synthetic root without passing through `avoid`.
    private static HashSet<int> ReachableAvoiding(HeapGraph g, int avoid)
    {
        var seen = new HashSet<int> { g.Root };
        var stack = new Stack<int>([g.Root]);
        while (stack.TryPop(out int node))
        {
            foreach (int next in g.Successors(node))
            {
                if (next != avoid && seen.Add(next))
                {
                    stack.Push(next);
                }
            }
        }
        return seen;
    }
}
