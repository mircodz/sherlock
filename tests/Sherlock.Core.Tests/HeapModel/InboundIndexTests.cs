using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Sherlock.Core.Tests.Common;
using Sherlock.Heap;
using Xunit;

namespace Sherlock.Core.Tests.HeapModel;

public sealed class InboundIndexTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static HeapGraph RandomGraph(Random random, out List<int>[] successors)
    {
        int n = random.Next(1, 30);
        successors = new List<int>[n + 1];
        for (int i = 0; i < n; i++)
        {
            successors[i] = Enumerable.Range(0, random.Next(0, 6)).Select(_ => random.Next(n)).ToList();
        }
        successors[n] = Enumerable.Range(0, n).Where(_ => random.Next(4) == 0).ToList(); // the synthetic root's
        var offsets = new int[n + 2];
        var edges = new List<int>();
        for (int i = 0; i <= n; i++)
        {
            offsets[i] = edges.Count;
            edges.AddRange(successors[i]);
        }
        offsets[n + 1] = edges.Count;
        return new HeapGraph(Enumerable.Range(0, n).Select(i => 0x1000UL + (ulong)i * 0x20).ToArray(), new uint[n], offsets, edges.ToArray());
    }

    [Fact]
    public void ListsEveryReferrerInOrderIncludingTheRoot()
    {
        var random = new Random(11);
        for (int round = 0; round < 300; round++)
        {
            HeapGraph graph = RandomGraph(random, out List<int>[] successors);

            using InboundIndex index = InboundIndex.Open(graph, _tmp.File(), CancellationToken.None);

            Assert.Equal(graph.NodeCount, index.NodeCount);
            for (int target = 0; target < graph.NodeCount; target++)
            {
                int[] expected = Enumerable.Range(0, graph.NodeCount).SelectMany(source => successors[source].Where(t => t == target).Select(_ => source)).ToArray();
                Assert.Equal(expected, index.Of(target).ToArray());
            }
        }
    }

    [Fact]
    public void ReusesTheIndexOfItsGraphAndRebuildsAnyOther()
    {
        string path = _tmp.File();
        HeapGraph graph = RandomGraph(new Random(3), out _);
        HeapGraph other = RandomGraph(new Random(4), out _);
        InboundIndex.Build(other, path, CancellationToken.None);

        Assert.Null(InboundIndex.Load(path, graph));
        using (InboundIndex rebuilt = InboundIndex.Open(graph, path, CancellationToken.None))
        {
            Assert.Equal(graph.NodeCount, rebuilt.NodeCount);
        }
        DateTime written = File.GetLastWriteTimeUtc(path);
        using InboundIndex reused = InboundIndex.Open(graph, path, CancellationToken.None);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }
}
