using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>
/// A computed dominator tree over the managed heap, with retained sizes.
///
/// An object X <i>dominates</i> Y if every path from a GC root to Y passes
/// through X. The <i>retained size</i> of X is its own size plus the sizes of
/// everything it dominates — i.e. the memory freed if X became unreachable.
///
/// Internally everything is indexed in reverse-postorder (RPO) space, where the
/// synthetic root (which points at every GC root) is node 0 and a node's
/// immediate dominator always has a smaller RPO number than the node itself.
/// </summary>
public sealed class DominatorTree
{
    private readonly ClrHeap _heap;
    private readonly ulong[] _address;              // RPO -> object address (0 for synthetic root)
    private readonly ulong[] _ownSize;              // RPO -> shallow size
    private readonly ulong[] _retained;             // RPO -> retained size
    private readonly int[] _idom;                   // RPO -> immediate dominator (RPO)
    private readonly Dictionary<ulong, int> _rpoOf; // address -> RPO (excludes synthetic root)

    internal DominatorTree(ClrHeap heap, ulong[] address, ulong[] ownSize, ulong[] retained, int[] idom, Dictionary<ulong, int> rpoOf)
    {
        _heap = heap;
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

    /// <summary>The objects with the largest retained size — the biggest memory holders.</summary>
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

    /// <summary>
    /// The objects immediately dominated by <paramref name="address"/> (its children
    /// in the dominator tree), largest retained first — i.e. what it directly holds onto.
    /// </summary>
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
    /// Builds a pruned view of the dominator tree suitable for a pprof-style graph:
    /// the <paramref name="maxNodes"/> heaviest objects (by retained size), each
    /// re-attached to its nearest surviving ancestor so the result stays a single
    /// connected tree hanging off the synthetic "GC roots" node.
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
            // Climb the immediate-dominator chain (which strictly decreases toward
            // the synthetic root at 0) until we hit another included node or the root.
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

    private string TypeNameAt(int rpo) =>
        _heap.GetObject(_address[rpo]).Type?.Name ?? "<unknown>";
}

/// <summary>
/// A node in a pruned dominator graph. <see cref="Id"/> is a stable identifier
/// (the tree's internal RPO number) usable for graph node names; <see cref="ParentId"/>
/// is the nearest surviving ancestor, or <c>null</c> when it hangs directly off the
/// synthetic GC-roots node.
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
