using System;
using System.Collections.Generic;

namespace Sherlock.Heap.Analysis;

/// <summary>
/// X dominates Y when every GC-root path to Y passes through X; its retained size includes everything it
/// dominates. Each object's dominated children are kept largest first, and no child retains more than its
/// parent, so every query here reads only the objects it returns.
/// </summary>
public sealed class DominatorTree : IDisposable
{
    private readonly HeapGraph _graph;
    private readonly DominatorAnalyzer.DominatorResult _result;
    private readonly IDisposable? _backing;

    internal DominatorTree(HeapGraph graph, DominatorAnalyzer.DominatorResult result, IDisposable? backing = null)
    {
        _graph = graph;
        _result = result;
        _backing = backing;
    }

    /// <summary>Number of reachable objects in the tree (excluding the synthetic root).</summary>
    public int ObjectCount => _result.Children.Length;

    internal DominatorAnalyzer.DominatorResult Result => _result;

    /// <summary>Total retained memory reachable from all GC roots.</summary>
    public ulong TotalReachableBytes => _result.Retained.Span[_graph.Root];

    /// <summary>Objects ordered by retained size, largest first.</summary>
    public IReadOnlyList<DominatorNode> TopDominators(int count)
    {
        var nodes = new List<DominatorNode>();
        foreach ((int node, _) in Largest(count))
        {
            nodes.Add(NodeAt(node));
        }
        return nodes;
    }

    /// <summary>Looks up a single object's retained size, or null if it isn't reachable.</summary>
    public DominatorNode? Find(ulong address) => Reachable(address, out int node) ? NodeAt(node) : null;

    /// <summary>Immediate dominator-tree children, largest retained size first.</summary>
    public IReadOnlyList<DominatorNode> ImmediateChildren(ulong address, int count)
    {
        if (!Reachable(address, out int parent))
        {
            return [];
        }
        ReadOnlySpan<int> children = ChildrenOf(parent);
        var nodes = new List<DominatorNode>(Math.Min(count, children.Length));
        foreach (int child in children[..Math.Min(count, children.Length)])
        {
            nodes.Add(NodeAt(child));
        }
        return nodes;
    }

    /// <summary>
    /// Keeps the heaviest objects by retained size, attaching each to its immediate dominator, which is always
    /// among them, or to the synthetic GC root.
    /// </summary>
    public DominatorGraph BuildGraph(int maxNodes)
    {
        var nodes = new List<DominatorGraphNode>();
        foreach ((int node, int parent) in Largest(Math.Max(1, maxNodes)))
        {
            DominatorNode n = NodeAt(node);
            nodes.Add(new DominatorGraphNode(node, n.Address, n.TypeName, n.OwnSize, n.RetainedSize, parent == _graph.Root ? null : parent));
        }
        return new DominatorGraph(nodes, TotalReachableBytes);
    }

    /// <summary>
    /// Returns the immediate-dominator chain, root-most object first and target last, excluding the
    /// synthetic root. Returns null when the target is unreachable from GC roots.
    /// </summary>
    /// <remarks>
    /// This is a dominator path, not necessarily a chain of direct object references.
    /// </remarks>
    public IReadOnlyList<(ulong Address, string TypeName)>? RetentionPath(ulong address)
    {
        if (!Reachable(address, out int node))
        {
            return null;
        }
        ReadOnlySpan<int> idom = _result.Idom.Span;
        var chain = new List<(ulong, string)>();
        for (int cur = node; cur != _graph.Root; cur = idom[cur])
        {
            chain.Add((_graph.Addresses.Span[cur], _graph.TypeNameOf(cur)));
        }
        chain.Reverse();
        return chain;
    }

    public void Dispose() => _backing?.Dispose();

    // The `count` largest objects by retained size, largest first, with their immediate dominators. Children are
    // sorted and never retain more than their parent, so taking the largest of the frontier (each expanded
    // object's first child and the next sibling of each taken one) yields objects in order.
    private IEnumerable<(int Node, int Parent)> Largest(int count)
    {
        ReadOnlySpan<long> starts = _result.ChildOffsets.Span;
        var frontier = new PriorityQueue<(int Parent, long Slot), ulong>(Comparer<ulong>.Create(static (a, b) => b.CompareTo(a)));
        Push(frontier, _graph.Root, starts[_graph.Root]);
        var taken = new List<(int, int)>();
        while (taken.Count < count && frontier.TryDequeue(out (int Parent, long Slot) entry, out _))
        {
            int node = _result.Children.Span[(int)entry.Slot];
            taken.Add((node, entry.Parent));
            Push(frontier, entry.Parent, entry.Slot + 1);
            Push(frontier, node, starts[node]);
        }
        return taken;
    }

    private void Push(PriorityQueue<(int Parent, long Slot), ulong> frontier, int parent, long slot)
    {
        if (slot < _result.ChildOffsets.Span[parent + 1])
        {
            frontier.Enqueue((parent, slot), _result.Retained.Span[_result.Children.Span[(int)slot]]);
        }
    }

    private ReadOnlySpan<int> ChildrenOf(int node)
    {
        ReadOnlySpan<long> starts = _result.ChildOffsets.Span;
        return _result.Children.Span[(int)starts[node]..(int)starts[node + 1]];
    }

    private bool Reachable(ulong address, out int node)
    {
        node = _graph.IndexOf(address);
        return node >= 0 && _result.Idom.Span[node] >= 0;
    }

    private DominatorNode NodeAt(int node) =>
        new(_graph.Addresses.Span[node], _graph.TypeNameOf(node), _graph.SizeOf(node), _result.Retained.Span[node]);
}

/// <summary>
/// A pruned graph node identified by its object id. <see cref="ParentId"/> is its immediate dominator, or null for
/// a child of the synthetic GC root.
/// </summary>
public sealed record DominatorGraphNode(
    int Id,
    ulong Address,
    string TypeName,
    ulong OwnSize,
    ulong RetainedSize,
    int? ParentId);

/// <summary>A pruned dominator tree ready for export (e.g. to Graphviz DOT).</summary>
public sealed record DominatorGraph(
    IReadOnlyList<DominatorGraphNode> Nodes,
    ulong TotalReachableBytes);
