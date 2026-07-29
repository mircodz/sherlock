using System;
using Sherlock.Core.Analysis;
using Sherlock.Core.HeapModel;
using Sherlock.Core.Tests.Common;

namespace Sherlock.Core.Tests.HeapModel;

/// <summary>
/// Round-trips the derived dominator-tree cache and asserts the validity key rejects a stale cache
/// (one computed from a different graph). Guards the persisted derived cache — a silent mismatch here
/// would serve wrong retained sizes.
/// </summary>
public sealed class DominatorTreeStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _path;

    public DominatorTreeStoreTests() => _path = _tmp.File();

    public void Dispose() => _tmp.Dispose();

    // A simple diamond graph: root->A; A->B,C; B->D; C->D.
    private static HeapGraph DiamondGraph()
    {
        ulong[] addresses = [0x1000, 0x2000, 0x3000, 0x4000];
        uint[] sizes = [10, 20, 30, 40];
        int[] offsets = [0, 2, 3, 4, 4, 5];
        int[] edges = [1, 2, 3, 3, 0];
        return new HeapGraph(addresses, sizes, offsets, edges);
    }

    [Fact]
    public void RoundTrips_ResultIdenticalToCompute()
    {
        HeapGraph g = DiamondGraph();
        DominatorAnalyzerV2.DominatorResult computed = DominatorAnalyzerV2.Compute(g);

        DominatorTreeStore.Save(_path, computed, g.ContentHash);
        DominatorAnalyzerV2.DominatorResult? loaded = DominatorTreeStore.Load(_path, g);

        Assert.NotNull(loaded);
        Assert.Equal(computed.Address, loaded!.Value.Address);
        Assert.Equal(computed.Own, loaded.Value.Own);
        Assert.Equal(computed.Retained, loaded.Value.Retained);
        Assert.Equal(computed.Idom, loaded.Value.Idom);
        Assert.Equal(computed.NodeByRpo, loaded.Value.NodeByRpo);
    }

    [Fact]
    public void Load_RejectsMismatchedGraphHash()
    {
        HeapGraph g = DiamondGraph();
        DominatorTreeStore.Save(_path, DominatorAnalyzerV2.Compute(g), g.ContentHash ^ 0xDEADBEEF);

        // The stored key differs from the graph's actual hash → cache is invalid → null → recompute.
        Assert.Null(DominatorTreeStore.Load(_path, g));
    }

    [Fact]
    public void ContentHash_IsStableAndStructureSensitive()
    {
        // Same structure → same hash (deterministic, not per-process randomized).
        Assert.Equal(DiamondGraph().ContentHash, DiamondGraph().ContentHash);

        // A changed edge → different hash.
        ulong[] addresses = [0x1000, 0x2000, 0x3000, 0x4000];
        uint[] sizes = [10, 20, 30, 40];
        int[] offsets = [0, 2, 3, 4, 4, 5];
        int[] changed = [1, 2, 3, 2, 0]; // C->C instead of C->D
        var altered = new HeapGraph(addresses, sizes, offsets, changed);
        Assert.NotEqual(DiamondGraph().ContentHash, altered.ContentHash);
    }
}
