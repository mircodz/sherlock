using System;
using System.Threading;
using Sherlock.Core.Analysis;
using Sherlock.Core.HeapModel;
using Sherlock.Core.Tests.Common;
using Xunit;

namespace Sherlock.Core.Tests.HeapModel;

public sealed class DominatorTreeStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _path;

    public DominatorTreeStoreTests() => _path = _tmp.File();

    public void Dispose() => _tmp.Dispose();

    // A diamond graph: root->A; A->B,C; B->D; C->D.
    private static HeapGraph DiamondGraph()
    {
        ulong[] addresses = [0x1000, 0x2000, 0x3000, 0x4000];
        uint[] sizes = [10, 20, 30, 40];
        int[] offsets = [0, 2, 3, 4, 4, 5];
        int[] edges = [1, 2, 3, 3, 0];
        return new HeapGraph(addresses, sizes, offsets, edges);
    }

    private DominatorTree Compute(HeapGraph g)
    {
        using InboundIndex inbound = InboundIndex.Open(g, _tmp.File(), CancellationToken.None);
        return DominatorAnalyzer.Compute(g, inbound, _path, CancellationToken.None);
    }

    [Fact]
    public void ReopensTheTreeItWrote()
    {
        HeapGraph g = DiamondGraph();
        using DominatorTree computed = Compute(g);

        using DominatorTree loaded = DominatorTreeStore.Load(_path, g)!;

        Assert.Equal(computed.ObjectCount, loaded.ObjectCount);
        Assert.Equal(computed.TotalReachableBytes, loaded.TotalReachableBytes);
        Assert.Equal(computed.TopDominators(10), loaded.TopDominators(10));
        Assert.Equal(computed.ImmediateChildren(0x1000, 10), loaded.ImmediateChildren(0x1000, 10));
    }

    [Fact]
    public void Load_RejectsATreeOfAnotherGraph()
    {
        using (Compute(DiamondGraph()))
        {
        }

        Assert.Null(DominatorTreeStore.Load(_path, DiamondGraph()));
    }

    [Fact]
    public void AGraphKeepsItsIdWhenSavedAndLoaded()
    {
        HeapGraph g = DiamondGraph();
        string graphPath = _path + ".graph";
        HeapGraphStore.Save(graphPath, g);
        using HeapGraph loaded = HeapGraphStore.Load(graphPath)!;

        Assert.Equal(g.Id, loaded.Id);
        Assert.NotEqual(g.Id, DiamondGraph().Id);
    }
}
