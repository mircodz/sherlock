using System.Collections.Generic;
using System.Linq;

namespace Sherlock.Core.Profiling;

/// <summary>
/// A top-down allocation call tree folded from an <see cref="AllocationProfile"/>: each
/// node aggregates the allocated/survived bytes of every call path passing through it
/// (inclusive), so you can navigate from the roots down to where allocation concentrates -
/// far more legible than a flat list keyed by leaf method.
/// </summary>
public sealed class AllocationTreeNode(string frame)
{
    private readonly Dictionary<string, AllocationTreeNode> _children = new();

    public string Frame { get; } = frame;
    public long AllocBytes { get; private set; }
    public long SurvivedBytes { get; private set; }
    public long AllocCount { get; private set; }

    /// <summary>Children ordered by allocated bytes, largest first.</summary>
    public IReadOnlyList<AllocationTreeNode> Children =>
        _children.Values.OrderByDescending(c => c.AllocBytes).ToList();

    private AllocationTreeNode Descend(string frame)
    {
        if (!_children.TryGetValue(frame, out AllocationTreeNode? child))
        {
            child = new AllocationTreeNode(frame);
            _children[frame] = child;
        }
        return child;
    }

    /// <summary>
    /// Folds an inverted caller tree rooted at <paramref name="method"/>: children are its
    /// immediate callers, weighted by what allocated while passing through it (back-traces).
    /// </summary>
    public static AllocationTreeNode BuildCallers(AllocationProfile profile, string method)
    {
        var root = new AllocationTreeNode(method);
        foreach (AllocationSite site in profile.Sites)
        {
            int at = -1;
            for (int i = site.Frames.Count - 1; i >= 0; i--) // deepest occurrence
            {
                if (site.Frames[i] == method) { at = i; break; }
            }
            if (at < 0)
            {
                continue;
            }

            AllocationTreeNode node = root;
            node.AllocBytes += site.AllocBytes;
            node.SurvivedBytes += site.SurvivedBytes;
            node.AllocCount += site.AllocCount;
            for (int i = at - 1; i >= 0; i--) // walk up: immediate caller first
            {
                node = node.Descend(site.Frames[i]);
                node.AllocBytes += site.AllocBytes;
                node.SurvivedBytes += site.SurvivedBytes;
                node.AllocCount += site.AllocCount;
            }
        }
        return root;
    }

    /// <summary>Folds a profile into a call tree rooted at a synthetic node.</summary>
    public static AllocationTreeNode Build(AllocationProfile profile)
    {
        var root = new AllocationTreeNode("(roots)");
        foreach (AllocationSite site in profile.Sites)
        {
            AllocationTreeNode node = root;
            node.AllocBytes += site.AllocBytes;
            node.SurvivedBytes += site.SurvivedBytes;
            node.AllocCount += site.AllocCount;
            foreach (string frame in site.Frames) // stored root -> leaf
            {
                node = node.Descend(frame);
                node.AllocBytes += site.AllocBytes;
                node.SurvivedBytes += site.SurvivedBytes;
                node.AllocCount += site.AllocCount;
            }
        }
        return root;
    }
}
