using System;
using System.Linq;
using System.Threading;
using Sherlock.Core.HeapModel;
using Sherlock.Core.Tests.Common;
using Xunit;

namespace Sherlock.Core.Tests.HeapModel;

public sealed class HeapGraphInstanceTests
{
    private static HeapGraph SampleGraph() =>
        new(new ulong[] { 0x1000, 0x2000, 0x3000, 0x4000, 0x5000 },
            new uint[] { 24, 80, 160, 40, 64 }, new int[7], [],
            new int[] { 0, 1, 2, 1, 3 },
            ["System.String", "App.Order", "App.Order[]", "App.Order"],
            freeBytes: 1024, freeCount: 2);

    [Fact]
    public void MatchesTypeIdsAndReturnsLargestObjectsWithFullTotals()
    {
        using HeapGraph graph = SampleGraph();

        InstanceListing result = Assert.IsType<InstanceListing>(graph.ListInstances("oRdEr", 2));

        Assert.Equal(4, result.TotalMatched);
        Assert.Equal(344UL, result.TotalMatchedSize);
        Assert.Equal(new ulong[] { 0x3000, 0x2000 }, result.Instances.Select(o => o.Address));
        Assert.Equal(new ulong[] { 160, 80 }, result.Instances.Select(o => o.Size));
        Assert.Equal(new[] { "App.Order[]", "App.Order" }, result.Instances.Select(o => o.TypeName));
        Assert.All(result.Instances, instance => Assert.Null(instance.Preview));
    }

    [Fact]
    public void LimitLargerThanPopulationReturnsAllMatches()
    {
        using HeapGraph graph = SampleGraph();

        InstanceListing result = Assert.IsType<InstanceListing>(graph.ListInstances("Order", 100));

        Assert.Equal(4, result.Instances.Count);
        Assert.Equal(new ulong[] { 160, 80, 64, 40 }, result.Instances.Select(o => o.Size));
        Assert.Equal(344UL, result.TotalMatchedSize);
    }

    [Fact]
    public void ZeroLimitStillCountsAllMatchingObjects()
    {
        using HeapGraph graph = SampleGraph();

        InstanceListing result = Assert.IsType<InstanceListing>(graph.ListInstances("Order", 0));

        Assert.Empty(result.Instances);
        Assert.Equal(4, result.TotalMatched);
        Assert.Equal(344UL, result.TotalMatchedSize);
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Free")]
    public void NoObjectsMatchReturnsAnEmptyListing(string filter)
    {
        using HeapGraph graph = SampleGraph();

        InstanceListing result = Assert.IsType<InstanceListing>(graph.ListInstances(filter));

        Assert.Empty(result.Instances);
        Assert.Equal(0, result.TotalMatched);
        Assert.Equal(0UL, result.TotalMatchedSize);
    }

    [Fact]
    public void EmptyTypedGraphReturnsAnEmptyListing()
    {
        using var graph = new HeapGraph(Array.Empty<ulong>(), Array.Empty<uint>(), new int[2], [],
            Array.Empty<int>(), ["App.Order"]);

        InstanceListing result = Assert.IsType<InstanceListing>(graph.ListInstances("Order"));

        Assert.Empty(result.Instances);
        Assert.Equal(0, result.TotalMatched);
        Assert.Equal(0UL, result.TotalMatchedSize);
    }

    [Fact]
    public void MissingTypeColumnsReturnNullForTheClrMdFallback()
    {
        using var graph = new HeapGraph(new ulong[] { 0x1000 }, new uint[] { 24 }, new int[3], []);

        Assert.Null(graph.ListInstances("Order"));
    }

    [Fact]
    public void EqualSizesKeepTheFirstMatchingObjects()
    {
        using var graph = new HeapGraph(new ulong[] { 0x1000, 0x2000, 0x3000 },
            new uint[] { 24, 24, 24 }, new int[5], [], new int[3], ["App.Order"]);

        InstanceListing result = Assert.IsType<InstanceListing>(graph.ListInstances("Order", 2));

        Assert.Equal(new ulong[] { 0x1000, 0x2000 }, result.Instances.Select(o => o.Address).Order());
        Assert.Equal(3, result.TotalMatched);
        Assert.Equal(72UL, result.TotalMatchedSize);
    }

    [Fact]
    public void TotalSizeDoesNotOverflowTheSizeColumn()
    {
        using var graph = new HeapGraph(new ulong[] { 0x1000, 0x2000 },
            new uint[] { uint.MaxValue, uint.MaxValue }, new int[4], [], new int[2], ["App.Order"]);

        InstanceListing result = Assert.IsType<InstanceListing>(graph.ListInstances("Order", 1));

        Assert.Single(result.Instances);
        Assert.Equal(2, result.TotalMatched);
        Assert.Equal(2UL * uint.MaxValue, result.TotalMatchedSize);
    }

    [Fact]
    public void PersistedTypeColumnsReturnTheSameInstances()
    {
        using var tmp = new TempDir();
        using HeapGraph graph = SampleGraph();
        string path = tmp.File();
        HeapGraphStore.Save(path, graph);
        using HeapGraph loaded = Assert.IsType<HeapGraph>(HeapGraphStore.Load(path));

        InstanceListing expected = Assert.IsType<InstanceListing>(graph.ListInstances("Order", 3));
        InstanceListing actual = Assert.IsType<InstanceListing>(loaded.ListInstances("Order", 3));

        Assert.Equal(expected.TotalMatched, actual.TotalMatched);
        Assert.Equal(expected.TotalMatchedSize, actual.TotalMatchedSize);
        Assert.Equal(expected.Instances.ToArray(), actual.Instances.ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyFilterIsRejected(string? filter)
    {
        using HeapGraph graph = SampleGraph();

        Assert.ThrowsAny<ArgumentException>(() => graph.ListInstances(filter!));
    }

    [Fact]
    public void NegativeLimitIsRejected()
    {
        using HeapGraph graph = SampleGraph();

        Assert.Throws<ArgumentOutOfRangeException>(() => graph.ListInstances("Order", -1));
    }

    [Theory]
    [InlineData("Order")]
    [InlineData("Missing")]
    public void CancelledQueryThrowsInsteadOfReturningPartialResults(string filter)
    {
        using HeapGraph graph = SampleGraph();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => graph.ListInstances(filter, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void DisposedGraphRejectsQueries()
    {
        HeapGraph graph = SampleGraph();
        graph.Dispose();

        Assert.Throws<ObjectDisposedException>(() => graph.ListInstances("Order"));
    }

    [Fact]
    public void SelectionDoesNotAllocatePerMatchingObject()
    {
        const int count = 200_000;
        var addresses = new ulong[count];
        var sizes = new uint[count];
        for (int i = 0; i < count; i++)
        {
            addresses[i] = 0x1000 + (ulong)i * 256;
            sizes[i] = (uint)(24 + i % 128);
        }
        using var graph = new HeapGraph(addresses, sizes, new int[count + 2], [], new int[count], ["App.Order"]);
        graph.ListInstances("Order", 20);

        long before = GC.GetAllocatedBytesForCurrentThread();
        InstanceListing? result = graph.ListInstances("Order", 20);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(result);
        Assert.Equal(count, result.TotalMatched);
        Assert.Equal(20, result.Instances.Count);
        Assert.InRange(allocated, 0, 128 * 1024);
    }
}
