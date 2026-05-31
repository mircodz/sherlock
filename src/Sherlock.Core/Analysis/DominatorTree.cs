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
    private readonly ulong[] _address;     // RPO -> object address (0 for synthetic root)
    private readonly ulong[] _ownSize;     // RPO -> shallow size
    private readonly ulong[] _retained;    // RPO -> retained size
    private readonly int[] _idom;          // RPO -> immediate dominator (RPO)
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
            return Array.Empty<DominatorNode>();

        var children = new List<int>();
        for (int rpo = 1; rpo < _idom.Length; rpo++)
        {
            if (rpo != parent && _idom[rpo] == parent)
                children.Add(rpo);
        }

        return children
            .OrderByDescending(rpo => _retained[rpo])
            .Take(count)
            .Select(NodeAt)
            .ToList();
    }

    private DominatorNode NodeAt(int rpo)
    {
        ulong address = _address[rpo];
        string typeName = _heap.GetObject(address).Type?.Name ?? "<unknown>";
        return new DominatorNode(address, typeName, _ownSize[rpo], _retained[rpo]);
    }
}
