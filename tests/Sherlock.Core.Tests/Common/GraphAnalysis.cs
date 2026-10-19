using System.Collections.Generic;
using System.Threading;
using Sherlock.Core.Analysis;
using Sherlock.Core.HeapModel;

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
}
