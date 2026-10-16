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
    private const ulong MB = 1 << 20;

    [Theory]
    [InlineData(99, null)]
    [InlineData(100, FindingSeverity.Warning)]
    [InlineData(499, FindingSeverity.Warning)]
    [InlineData(500, FindingSeverity.High)]
    public void QuickFindings_RetentionUsesReachableBytesAndExistingThresholds(int retainedMegabytes, FindingSeverity? severity)
    {
        DominatorTree tree = LargestRetainer((ulong)retainedMegabytes * MB);
        IReadOnlyList<Finding> findings = HeapDoctor.QuickFindings([new("App.Widget", 1, 1_000 * MB)], tree);
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
        Assert.Equal(retainedMegabytes * (long)MB, finding.Bytes);
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
        Finding finding = Assert.Single(HeapDoctor.QuickFindings([], RootedTree((type, 100 * MB))));

        Assert.StartsWith($"{TypeNames.Short(type)} collection retains ", finding.Title);
        Assert.Equal(type, finding.Type);
    }

    [Fact]
    public void QuickFindings_ChoosesOnlyTheLargestRetainer()
    {
        DominatorTree tree = RootedTree(("App.Small", 100 * MB), ("App.Large", 900 * MB));

        Finding finding = Assert.Single(HeapDoctor.QuickFindings([], tree));

        Assert.Equal("App.Large", finding.Type);
        Assert.Equal(0x1100UL, finding.Address);
        Assert.Equal(900 * (long)MB, finding.Bytes);
        Assert.Contains("(90% of the reachable heap)", finding.Title);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(249)]
    [InlineData(250)]
    [InlineData(1000)]
    public void QuickFindings_FragmentationIncludesFreeSpaceInHeapTotal(int freeMegabytes)
    {
        HeapTypeStat[] histogram = [new("System.Byte[]", 1, (ulong)(1000 - freeMegabytes) * MB), new("Free", 2, (ulong)freeMegabytes * MB)];

        IReadOnlyList<Finding> findings = HeapDoctor.QuickFindings(histogram, RootedTree());
        if (freeMegabytes < 250)
        {
            Assert.Empty(findings);
            return;
        }

        Finding finding = Assert.Single(findings);
        Assert.Equal("fragmentation", finding.Category);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(freeMegabytes * (long)MB, finding.Bytes);
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
        HeapTypeStat[] histogram = [new("Free", 1, 250 * MB), new("App.Widget", 10_000, 750 * MB)];

        IReadOnlyList<Finding> findings = HeapDoctor.QuickFindings(histogram, LargestRetainer(100 * MB));

        Assert.Equal(["retention", "fragmentation", "growth"], findings.Select(finding => finding.Category));
        Assert.Equal([FindingSeverity.Warning, FindingSeverity.Warning, FindingSeverity.Info],
            findings.Select(finding => finding.Severity));
    }

    [Fact]
    public void QuickFindings_SmallHeapsHaveNoRetentionOrFragmentationFindings()
    {
        // The same shares that are findings on a 1,000 MB heap: a small heap is mostly one holder and free space.
        HeapTypeStat[] histogram = [new("Free", 1, 300_000), new("App.Widget", 1, 700_000)];

        Assert.Empty(HeapDoctor.QuickFindings(histogram, LargestRetainer(900_000)));
    }

    [Fact]
    public void Diagnose_PropagatesCancellationBeforeAccessingTheSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = Assert.Throws<OperationCanceledException>(() => new HeapDoctor(null!).Diagnose(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    // The largest of objects totalling 1,000 MB, or 1 MB when the retainer is smaller than that.
    private static DominatorTree LargestRetainer(ulong bytes)
    {
        ulong total = bytes < MB ? MB : 1_000 * MB;
        var objects = new List<(string Type, ulong Bytes)> { ("App.Widget", bytes) };
        for (ulong remaining = total - bytes; remaining > 0;)
        {
            ulong size = Math.Min(remaining, total / 20);
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
