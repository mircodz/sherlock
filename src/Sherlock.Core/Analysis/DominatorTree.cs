using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>
/// X dominates Y when every GC-root path to Y passes through X; its retained size includes everything
/// it dominates. Columns use reverse-postorder (RPO): the synthetic GC root is 0, and each immediate
/// dominator has a smaller RPO than its child.
/// </summary>
public sealed class DominatorTree
{
    private readonly ClrHeap _heap;
    private readonly Func<ulong, string>? _typeNameByAddress;
    private readonly ulong[] _address;              // RPO -> object address (0 for synthetic root)
    private readonly ulong[] _ownSize;              // RPO -> shallow size
    private readonly ulong[] _retained;             // RPO -> retained size
    private readonly int[] _idom;                   // RPO -> immediate dominator (RPO)
    private readonly Dictionary<ulong, int> _rpoOf; // address -> RPO (excludes synthetic root)

    internal DominatorTree(ClrHeap heap, ulong[] address, ulong[] ownSize, ulong[] retained, int[] idom,
        Dictionary<ulong, int> rpoOf, Func<ulong, string>? typeNameByAddress = null)
    {
        _heap = heap;
        _typeNameByAddress = typeNameByAddress;
        _address = address;
        _ownSize = ownSize;
        _retained = retained;
        _idom = idom;
        _rpoOf = rpoOf;
    }

    /// <summary>Number of reachable objects in the tree (excluding the synthetic root).</summary>
    public int ObjectCount => _address.Length - 1;

    /// <summary>Total retained memory reachable from all GC roots.</summary>
    public ulong TotalReachableBytes => _retained.Length == 0 ? 0 : _retained[0];

    /// <summary>Objects ordered by retained size, largest first.</summary>
    public IReadOnlyList<DominatorNode> TopDominators(int count)
    {
        return Enumerable.Range(1, _address.Length - 1)
            .OrderByDescending(rpo => _retained[rpo])
            .Take(count)
            .Select(NodeAt)
            .ToList();
    }

    /// <summary>Looks up a single object's retained size, or null if it isn't reachable.</summary>
    public DominatorNode? Find(ulong address) =>
        _rpoOf.TryGetValue(address, out int rpo) ? NodeAt(rpo) : null;

    /// <summary>Immediate dominator-tree children, largest retained size first.</summary>
    public IReadOnlyList<DominatorNode> ImmediateChildren(ulong address, int count)
    {
        if (!_rpoOf.TryGetValue(address, out int parent))
        {
            return [];
        }

        var children = new List<int>();
        for (int rpo = 1; rpo < _idom.Length; rpo++)
        {
            if (rpo != parent && _idom[rpo] == parent)
            {
                children.Add(rpo);
            }
        }

        return children
            .OrderByDescending(rpo => _retained[rpo])
            .Take(count)
            .Select(NodeAt)
            .ToList();
    }

    /// <summary>
    /// Keeps the heaviest objects by retained size, attaching each to its nearest included ancestor
    /// or the synthetic GC root.
    /// </summary>
    public DominatorGraph BuildGraph(int maxNodes)
    {
        if (maxNodes < 1)
        {
            maxNodes = 1;
        }

        List<int> top = Enumerable.Range(1, _address.Length - 1)
            .OrderByDescending(rpo => _retained[rpo])
            .Take(maxNodes)
            .ToList();

        var included = new HashSet<int>(top);

        var nodes = new List<DominatorGraphNode>(top.Count);
        foreach (int rpo in top)
        {
            int parent = _idom[rpo];
            while (parent != 0 && !included.Contains(parent))
            {
                parent = _idom[parent];
            }

            int? parentId = parent == 0 ? null : parent;
            nodes.Add(new DominatorGraphNode(rpo, _address[rpo], TypeNameAt(rpo), _ownSize[rpo], _retained[rpo], parentId));
        }

        return new DominatorGraph(nodes, TotalReachableBytes);
    }

    private DominatorNode NodeAt(int rpo) =>
        new(_address[rpo], TypeNameAt(rpo), _ownSize[rpo], _retained[rpo]);

    /// <summary>
    /// Returns the immediate-dominator chain, root-most object first and target last, excluding the
    /// synthetic root. Returns null when the target is unreachable from GC roots.
    /// </summary>
    /// <remarks>
    /// This is a dominator path, not necessarily a chain of direct object references.
    /// </remarks>
    public IReadOnlyList<(ulong Address, string TypeName)>? RetentionPath(ulong address)
    {
        if (!_rpoOf.TryGetValue(address, out int rpo))
        {
            return null;
        }

        var chain = new List<(ulong, string)>();
        for (int cur = rpo; cur != 0; cur = _idom[cur])
        {
            chain.Add((_address[cur], TypeNameAt(cur)));
        }
        chain.Reverse();
        return chain;
    }

    private string TypeNameAt(int rpo) =>
        _typeNameByAddress?.Invoke(_address[rpo]) ?? _heap.GetObject(_address[rpo]).Type?.Name ?? "<unknown>";
}

/// <summary>
/// A pruned graph node identified by its RPO. <see cref="ParentId"/> is the nearest included ancestor,
/// or null for a child of the synthetic GC root.
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
