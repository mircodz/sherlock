using System.Collections.Generic;
using System.Threading;

namespace Sherlock.Core.Analysis;

/// <summary>
/// The V2 GC-root finder. Like <see cref="DominatorAnalyzerV2"/> vs <see cref="DominatorAnalyzer"/>, it
/// differs from <see cref="RootAnalyzer"/> only in <i>where the answer comes from</i>: V1 runs a
/// breadth-first search outward from every GC root through ClrMD's DAC (single-threaded, ~1-2M edges/s,
/// and on a large heap a single miss walks the whole reachable set — the source of the multi-minute TUI
/// freeze); V2 answers from the persisted dominator tree computed over the DAC-free <see cref="HeapModel.HeapGraph"/>.
///
/// The dominator tree's immediate-dominator chain <i>is</i> a retention path: every object on it must
/// stay alive for the target to stay alive. So "why is this object alive" becomes an O(path length) walk
/// of an array that's already cached on disk beside the dump — no heap traversal, and instant on reopen.
///
/// Trade-off: this returns the single dominator path (the chain of necessary holders), not the several
/// shortest arbitrary reference chains V1's BFS could enumerate. That's exactly the "what retains this"
/// answer the gcroot view wants; true multi-/shortest-path search needs a persisted reverse-edge column
/// (a later tier). Kept separate from V1 until it's the default.
/// </summary>
public sealed class RootAnalyzerV2(DumpSession session)
{
    /// <summary>
    /// Returns the retention path that keeps <paramref name="targetAddress"/> alive, or an empty list if
    /// the object is not reachable from any GC root (collectable). At most one path is produced (the
    /// dominator path); <paramref name="maxPaths"/> is accepted for API parity with <see cref="RootAnalyzer"/>
    /// and currently caps the result at one.
    /// </summary>
    public IReadOnlyList<GcRootPath> FindRoots(ulong targetAddress, int maxPaths = 1, CancellationToken cancellationToken = default)
    {
        DominatorTree dom = session.GetDominatorTreeV2(cancellationToken);

        IReadOnlyList<(ulong Address, string TypeName)>? chain = dom.RetentionPath(targetAddress);
        if (chain is null || chain.Count == 0)
        {
            return [];
        }

        // The chain is root-most first, target last. The outermost object (chain[0]) is the one held
        // directly by a GC root; describe the root by that object so the path reads root -> ... -> target.
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
