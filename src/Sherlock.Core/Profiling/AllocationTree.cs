using System.Collections.Generic;
using System.Linq;

namespace Sherlock.Core.Profiling;

/// <summary>Top-down allocation call tree folded from an <see cref="AllocationProfile"/>: each node
/// holds the inclusive alloc/survived bytes of every call path through it.</summary>
public sealed class AllocationTreeNode
{
    private readonly Dictionary<MethodFrame, AllocationTreeNode> _children = [];

    private AllocationTreeNode(MethodFrame? frame) => Frame = frame;

    /// <summary>The node's method; null for the root, which stands for the whole tree.</summary>
    public MethodFrame? Frame { get; }
    public long AllocBytes { get; private set; }
    public long SurvivedBytes { get; private set; }
    public long AllocCount { get; private set; }

    /// <summary>Children ordered by allocated bytes, largest first.</summary>
    public IReadOnlyList<AllocationTreeNode> Children =>
        _children.Values.OrderByDescending(c => c.AllocBytes).ToList();

    private AllocationTreeNode Descend(MethodFrame frame)
    {
        if (!_children.TryGetValue(frame, out AllocationTreeNode? child))
        {
            child = new AllocationTreeNode(frame);
            _children[frame] = child;
        }
        return child;
    }

    private void Add(AllocationSite site)
    {
        AllocBytes += site.AllocBytes;
        SurvivedBytes += site.SurvivedBytes;
        AllocCount += site.AllocCount;
    }

    /// <summary>Folds an inverted caller tree of <paramref name="methods"/>: the root stands for them, its
    /// children are their immediate callers, weighted by what allocated while passing through them.</summary>
    public static AllocationTreeNode BuildCallers(AllocationProfile profile, IReadOnlyCollection<MethodFrame> methods)
    {
        HashSet<MethodFrame> set = [.. methods];
        var root = new AllocationTreeNode(null);
        foreach (AllocationSite site in profile.Sites)
        {
            int at = -1;
            for (int i = site.Frames.Count - 1; i >= 0; i--) // deepest occurrence first
            {
                if (set.Contains(site.Frames[i])) { at = i; break; }
            }
            if (at < 0)
            {
                continue;
            }

            AllocationTreeNode node = root;
            node.Add(site);
            for (int i = at - 1; i >= 0; i--) // walk up: immediate caller first
            {
                node = node.Descend(site.Frames[i]);
                node.Add(site);
            }
        }
        return root;
    }

    /// <summary>Folds a profile into a call tree whose root stands for every stack.</summary>
    public static AllocationTreeNode Build(AllocationProfile profile)
    {
        var root = new AllocationTreeNode(null);
        foreach (AllocationSite site in profile.Sites)
        {
            AllocationTreeNode node = root;
            node.Add(site);
            foreach (MethodFrame frame in site.Frames) // stored root -> leaf
            {
                node = node.Descend(frame);
                node.Add(site);
            }
        }
        return root;
    }
}
