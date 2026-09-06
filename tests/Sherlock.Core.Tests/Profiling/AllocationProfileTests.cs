using System;
using System.Linq;
using Sherlock.Core.Profiling;
using Xunit;

namespace Sherlock.Core.Tests.Profiling;

public sealed class AllocationProfileTests
{
    private static AllocationProfile Profile() => new([
        new(["Root", "Allocate", "Allocate"], 100, 2, 20, 1, "App.First"),
        new(["Root", "Allocate", "Other", "Allocate"], 200, 3, 50, 1, "App.Second"),
        new(["Root", "Allocate", "Other"], 400, 4, 100, 1, "App.First"),
        new(["OtherRoot", "Allocate"], 50, 1, 0, 0),
        new([], 900, 9, 300, 3, "App.First"),
    ]);

    [Fact]
    public void HotMethods_AggregatesLeavesAndCountsRecursiveFramesOncePerSite()
    {
        AllocationProfile profile = Profile();

        Assert.Equal([
            new AllocationMethodStat("Other", 400, 600, 4),
            new AllocationMethodStat("Allocate", 350, 750, 6),
        ], profile.HotMethods());
    }

    [Fact]
    public void HotMethods_ExcludesUnframedAllocationsWithoutChangingProfileTotals()
    {
        AllocationProfile profile = Profile();
        var methods = profile.HotMethods();

        Assert.Equal(750, methods.Sum(method => method.SelfBytes));
        Assert.Equal(10, methods.Sum(method => method.AllocCount));
        Assert.All(methods, method => Assert.Equal(profile.Through(method.Method).TotalAllocBytes, method.InclusiveBytes));
        Assert.Equal(1650, profile.TotalAllocBytes);
        Assert.Equal(470, profile.TotalSurvivedBytes);
        Assert.Equal(5, profile.Sites.Count);
    }

    [Fact]
    public void HotMethods_UsesOnlySitesInTheTypeProjection()
    {
        AllocationProfile profile = Profile();

        Assert.Equal([
            new AllocationMethodStat("Other", 400, 400, 4),
            new AllocationMethodStat("Allocate", 100, 500, 2),
        ], profile.OfType("App.First").HotMethods());
        Assert.Equal(new AllocationMethodStat("Allocate", 200, 200, 3),
            Assert.Single(profile.OfType("App.Second").HotMethods()));
        Assert.Empty(profile.OfType("App.Missing").HotMethods());
    }

    [Fact]
    public void HotMethods_CountsEveryTypeRecordForTheSameStack()
    {
        var profile = new AllocationProfile([
            new(["Root", "Allocate"], 100, 1, 0, 0, "App.First"),
            new(["Root", "Allocate"], 200, 2, 0, 0, "App.Second"),
            new(["Root", "Allocate"], 300, 3, 0, 0, "App.First"),
        ]);

        Assert.Equal(new AllocationMethodStat("Allocate", 600, 600, 6), Assert.Single(profile.HotMethods()));
        Assert.Equal(new AllocationMethodStat("Allocate", 400, 400, 4),
            Assert.Single(profile.OfType("App.First").HotMethods()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(20)]
    public void HotMethods_LimitsRowsAfterComputingCompleteTotals(int limit)
    {
        AllocationProfile profile = Profile();
        AllocationMethodStat[] expected = [
            new("Other", 400, 600, 4),
            new("Allocate", 350, 750, 6),
        ];

        Assert.Equal(expected.Take(limit), profile.HotMethods(limit));
        Assert.Equal(1650, profile.TotalAllocBytes);
    }

    [Fact]
    public void HotMethods_PreservesFirstLeafOrderForEqualSelfBytes()
    {
        var profile = new AllocationProfile([
            new(["TieA", "TieB"], 10, 1, 0, 0),
            new(["TieB", "TieA"], 10, 1, 0, 0),
            new(["TieA", "Largest"], 20, 1, 0, 0),
        ]);

        Assert.Equal(["Largest", "TieB", "TieA"], profile.HotMethods().Select(method => method.Method));
        Assert.Equal(["Largest", "TieB"], profile.HotMethods(2).Select(method => method.Method));
    }

    [Fact]
    public void HotMethods_PreservesOrdinalMethodNamesAndLongTotals()
    {
        var profile = new AllocationProfile([
            new(["Allocate"], 3_000_000_000, 3_000_000_000, 0, 0),
            new(["Allocate"], 4_000_000_000, 4_000_000_000, 0, 0),
            new(["allocate"], 0, 0, 0, 0),
        ]);

        Assert.Equal([
            new AllocationMethodStat("Allocate", 7_000_000_000, 7_000_000_000, 7_000_000_000),
            new AllocationMethodStat("allocate", 0, 0, 0),
        ], profile.HotMethods());
    }

    [Fact]
    public void HotMethods_ReturnsEmptyForProfilesWithoutManagedFrames()
    {
        Assert.Empty(new AllocationProfile([]).HotMethods());
        Assert.Empty(new AllocationProfile([new([], 100, 1, 0, 0)]).HotMethods());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void HotMethods_RejectsNegativeLimits(int limit)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new AllocationProfile([]).HotMethods(limit));

        Assert.Equal("limit", error.ParamName);
    }
}
