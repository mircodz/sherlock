using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Sherlock.Core.HeapModel;
using Xunit;

namespace Sherlock.Core.Tests.HeapModel;

public sealed class PredecessorsTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(1 << 30)]
    public void ListsEveryReferrerInOrderHoweverTheyAreSplit(int maxChunk)
    {
        var random = new Random(11);
        for (int round = 0; round < 300; round++)
        {
            int n = random.Next(1, 30);
            var successors = new List<int>[n];
            for (int i = 0; i < n; i++)
            {
                successors[i] = Enumerable.Range(0, random.Next(0, 6)).Select(_ => random.Next(n)).ToList();
            }
            var offsets = new int[n + 2];
            var edges = new List<int>();
            for (int i = 0; i < n; i++)
            {
                offsets[i] = edges.Count;
                edges.AddRange(successors[i]);
            }
            offsets[n] = offsets[n + 1] = edges.Count;
            var graph = new HeapGraph(Enumerable.Range(0, n).Select(i => 0x1000UL + (ulong)i * 0x20).ToArray(), new uint[n], offsets, edges.ToArray());

            Predecessors predecessors = Predecessors.Build(graph, CancellationToken.None, maxChunk);

            for (int target = 0; target < n; target++)
            {
                int[] expected = Enumerable.Range(0, n).SelectMany(source => successors[source].Where(t => t == target).Select(_ => source)).ToArray();
                Assert.Equal(expected, predecessors.Of(target).ToArray());
            }
        }
    }
}
