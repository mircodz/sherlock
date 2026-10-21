using System;
using System.Collections.Generic;
using System.Threading;
using Sherlock.Heap;
using Sherlock.Heap.Analysis;

namespace Sherlock.Core.Tests.Common;

/// <summary>Runs the on-disk analyses over a small in-memory graph, in a temporary directory.</summary>
internal static class GraphAnalysis
{
    public static IReadOnlyList<GcRootPath> Roots(HeapGraph graph, ulong address)
    {
        using var tmp = new TempDir();
        using InboundIndex inbound = InboundIndex.Open(graph, tmp.File(), CancellationToken.None);
        return RootAnalyzer.Find(graph, inbound, tmp.Path, address);
    }

    /// <summary>The graph's dominator tree; disposing it deletes its files.</summary>
    public static DominatorTree Dominators(HeapGraph graph)
    {
        var tmp = new TempDir();
        try
        {
            using InboundIndex inbound = InboundIndex.Open(graph, tmp.File(), CancellationToken.None);
            DominatorTree tree = DominatorAnalyzer.Compute(graph, inbound, tmp.File(), CancellationToken.None);
            return new DominatorTree(graph, tree.Result, new Owner(tree, tmp));
        }
        catch
        {
            tmp.Dispose();
            throw;
        }
    }

    private sealed class Owner(IDisposable tree, IDisposable directory) : IDisposable
    {
        public void Dispose()
        {
            tree.Dispose();
            directory.Dispose();
        }
    }
}
