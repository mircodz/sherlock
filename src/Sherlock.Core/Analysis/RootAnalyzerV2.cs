using System.Collections.Generic;
using System.Threading;

namespace Sherlock.Core.Analysis;

/// <summary>
/// The default GC-root finder, backed by the persisted dominator tree over the DAC-free
/// <see cref="HeapModel.HeapGraph"/>. Unlike <see cref="RootAnalyzer"/>, which BFSes outward from every
/// GC root through ClrMD's DAC (single-threaded, ~1-2M edges/s, a miss on a large heap walks the whole
/// reachable set and causes the multi-minute TUI freeze), this answers from an array already cached on
/// disk beside the dump: the dominator tree's immediate-dominator chain <i>is</i> a retention path, so
/// "why is this alive" is an O(path length) walk with no heap traversal, instant on reopen.
///
/// Trade-off: returns the single dominator path (the necessary holders), not the several shortest
/// arbitrary reference chains <see cref="RootAnalyzer"/>'s BFS can enumerate. That's the answer the
/// gcroot view wants; true multi-/shortest-path search needs a persisted reverse-edge column (a later
/// tier), so <see cref="RootAnalyzer"/> is kept as a fallback.
/// </summary>
public sealed class RootAnalyzerV2(DumpSession session)
{
    /// <summary>
    /// Returns the retention path that keeps <paramref name="targetAddress"/> alive, or empty if the
    /// object is unreachable (collectable). At most one path (the dominator path); <paramref name="maxPaths"/>
    /// is accepted for API parity with <see cref="RootAnalyzer"/> and currently caps the result at one.
    /// </summary>
    public IReadOnlyList<GcRootPath> FindRoots(ulong targetAddress, int maxPaths = 1, CancellationToken cancellationToken = default)
    {
        DominatorTree dom = session.GetDominatorTree(cancellationToken);

        IReadOnlyList<(ulong Address, string TypeName)>? chain = dom.RetentionPath(targetAddress);
        if (chain is null || chain.Count == 0)
        {
            return [];
        }

        // Chain is root-most first, target last. chain[0] is held directly by a GC root; describe the
        // root by it so the path reads root -> ... -> target.
        var path = new List<GcRootNode>(chain.Count);
        foreach ((ulong addr, string type) in chain)
        {
            path.Add(new GcRootNode(addr, type));
        }

        (ulong Address, string TypeName) held = chain[0];
        string rootDescription = $"GC root -> {held.TypeName} @ {held.Address:x12}";
        return [new GcRootPath(rootDescription, path)];
    }
}
