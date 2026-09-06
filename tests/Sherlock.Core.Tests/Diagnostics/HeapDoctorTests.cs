using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Sherlock.Core.Analysis;
using Sherlock.Core.Diagnostics;
using Xunit;

namespace Sherlock.Core.Tests.Diagnostics;

public sealed class HeapDoctorTests
{
    [Theory]
    [InlineData(99, null)]
    [InlineData(100, FindingSeverity.Warning)]
    [InlineData(499, FindingSeverity.Warning)]
    [InlineData(500, FindingSeverity.High)]
    public void QuickFindings_RetentionUsesReachableBytesAndExistingThresholds(int retainedBytes, FindingSeverity? severity)
    {
        DominatorTree tree = LargestRetainer((ulong)retainedBytes);
        IReadOnlyList<Finding> findings = HeapDoctor.QuickFindings([new("App.Widget", 1, 1_000_000)], tree);
        if (severity is null)
        {
            Assert.Empty(findings);
            return;
        }

        Finding finding = Assert.Single(findings);
        Assert.Equal("retention", finding.Category);
        Assert.Equal(severity, finding.Severity);
        Assert.Equal("App.Widget", finding.Type);
        Assert.Equal(0x1000UL, finding.Address);
        Assert.Equal((long)retainedBytes, finding.Bytes);
        Assert.Equal("gcroot 0x1000", finding.NextCommand);
        Assert.StartsWith("Widget retains ", finding.Title);
    }

    [Theory]
    [InlineData("System.Collections.Generic.List<App.Widget>")]
    [InlineData("System.Collections.Generic.Dictionary<System.String,App.Widget>")]
    [InlineData("System.Collections.Generic.HashSet<App.Widget>")]
    [InlineData("System.Collections.Generic.Queue<App.Widget>")]
    [InlineData("System.Collections.Generic.Stack<App.Widget>")]
    [InlineData("App.Widget[]")]
    public void QuickFindings_IdentifiesCollectionsAndKeepsGenericTypeNames(string type)
    {
        Finding finding = Assert.Single(HeapDoctor.QuickFindings([], RootedTree((type, 100))));

        Assert.StartsWith($"{TypeNames.Short(type)} collection retains ", finding.Title);
        Assert.Equal(type, finding.Type);
    }

    [Fact]
    public void QuickFindings_ChoosesOnlyTheLargestRetainer()
    {
        DominatorTree tree = RootedTree(("App.Small", 100), ("App.Large", 900));

        Finding finding = Assert.Single(HeapDoctor.QuickFindings([], tree));

        Assert.Equal("App.Large", finding.Type);
        Assert.Equal(0x1100UL, finding.Address);
        Assert.Equal(900L, finding.Bytes);
        Assert.Contains("(90% of the reachable heap)", finding.Title);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(249)]
    [InlineData(250)]
    [InlineData(1000)]
    public void QuickFindings_FragmentationIncludesFreeSpaceInHeapTotal(int freeBytes)
    {
        HeapTypeStat[] histogram = [new("System.Byte[]", 1, (ulong)(1000 - freeBytes)), new("Free", 2, (ulong)freeBytes)];

        IReadOnlyList<Finding> findings = HeapDoctor.QuickFindings(histogram, RootedTree());
        if (freeBytes < 250)
        {
            Assert.Empty(findings);
            return;
        }

        Finding finding = Assert.Single(findings);
        Assert.Equal("fragmentation", finding.Category);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal((long)freeBytes, finding.Bytes);
        Assert.Equal("segments", finding.NextCommand);
    }

    [Fact]
    public void QuickFindings_EmptyAndZeroSizedHeapsHaveNoFindings()
    {
        Assert.Empty(HeapDoctor.QuickFindings([], RootedTree()));
        Assert.Empty(HeapDoctor.QuickFindings([new("Free", 1, 0)], RootedTree(("App.Empty", 0))));
    }

    [Fact]
    public void QuickFindings_GrowthChoosesLargestEligibleUserTypeFromUnsortedHistogram()
    {
        HeapTypeStat[] histogram = [
            new("App.Small", 10_000, 20_000),
            new("System.String", 100_000, 5_000_000),
            new("Free", 100_000, 2),
            new("Microsoft.Cache", 100_000, 6_000_000),
            new("App.TooFew", 9_999, 10_000_000),
            new("App.Cache<App.Widget>", 10_000, 200_000),
        ];

        Finding finding = Assert.Single(HeapDoctor.QuickFindings(histogram, RootedTree()));

        Assert.Equal("growth", finding.Category);
        Assert.Equal(FindingSeverity.Info, finding.Severity);
        Assert.Equal("App.Cache<App.Widget>", finding.Type);
        Assert.Equal(200_000L, finding.Bytes);
        Assert.Equal(10_000L, finding.Count);
        Assert.Equal("objects Cache<App.Widget>", finding.NextCommand);
    }

    [Fact]
    public void QuickFindings_DoesNotReportGrowthBelowTheThreshold()
    {
        Assert.Empty(HeapDoctor.QuickFindings([new("App.Widget", 9_999, 100_000)], RootedTree()));
    }

    [Fact]
    public void QuickFindings_OrdersSeverityAndPreservesRuleOrderForTies()
    {
        HeapTypeStat[] histogram = [new("Free", 1, 250), new("App.Widget", 10_000, 750)];

        IReadOnlyList<Finding> findings = HeapDoctor.QuickFindings(histogram, LargestRetainer(100));

        Assert.Equal(["retention", "fragmentation", "growth"], findings.Select(finding => finding.Category));
        Assert.Equal([FindingSeverity.Warning, FindingSeverity.Warning, FindingSeverity.Info],
            findings.Select(finding => finding.Severity));
    }

    [Fact]
    public void Diagnose_PropagatesCancellationBeforeAccessingTheSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = Assert.Throws<OperationCanceledException>(() => new HeapDoctor(null!).Diagnose(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    private static DominatorTree LargestRetainer(ulong bytes)
    {
        var objects = new List<(string Type, ulong Bytes)> { ("App.Widget", bytes) };
        for (ulong remaining = 1000 - bytes; remaining > 0;)
        {
            ulong size = Math.Min(remaining, 50UL);
            objects.Add(("System.Object", size));
            remaining -= size;
        }
        return RootedTree(objects.ToArray());
    }

    // Independent root objects; type names resolve without a ClrHeap or dump.
    private static DominatorTree RootedTree(params (string Type, ulong Bytes)[] objects)
    {
        var address = new ulong[objects.Length + 1];
        var ownSize = new ulong[address.Length];
        var retained = new ulong[address.Length];
        var rpoOf = new Dictionary<ulong, int>();
        for (int i = 0; i < objects.Length; i++)
        {
            int rpo = i + 1;
            address[rpo] = 0x1000UL + (ulong)i * 0x100;
            ownSize[rpo] = retained[rpo] = objects[i].Bytes;
            retained[0] += objects[i].Bytes;
            rpoOf[address[rpo]] = rpo;
        }
        return new DominatorTree(null!, address, ownSize, retained, new int[address.Length], rpoOf,
            addr => objects[rpoOf[addr] - 1].Type);
    }
}
