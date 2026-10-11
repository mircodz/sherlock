using System;
using System.Linq;
using Sherlock.Core.Profiling;
using Xunit;
using static Sherlock.Tests.Frames;

namespace Sherlock.Core.Tests.Profiling;

public sealed class AllocationProfileTests
{
    private static AllocationProfile Profile() => new([
        new(Stack("Root", "Allocate", "Allocate"), "App.First", 100, 2, 20, 1),
        new(Stack("Root", "Allocate", "Other", "Allocate"), "App.Second", 200, 3, 50, 1),
        new(Stack("Root", "Allocate", "Other"), "App.First", 400, 4, 100, 1),
        new(Stack("OtherRoot", "Allocate"), "App.Third", 50, 1, 0, 0),
        new([], "App.First", 900, 9, 300, 3),
    ]);

    [Fact]
    public void HotMethods_AggregatesLeavesAndCountsRecursiveFramesOncePerSite()
    {
        AllocationProfile profile = Profile();

        Assert.Equal([
            new AllocationMethodStat(Method("Other"), 400, 600, 4),
            new AllocationMethodStat(Method("Allocate"), 350, 750, 6),
        ], profile.HotMethods());
    }

    [Fact]
    public void HotMethods_ExcludesUnframedAllocationsWithoutChangingProfileTotals()
    {
        AllocationProfile profile = Profile();
        var methods = profile.HotMethods();

        Assert.Equal(750, methods.Sum(method => method.SelfBytes));
        Assert.Equal(10, methods.Sum(method => method.AllocCount));
        Assert.All(methods, method => Assert.Equal(profile.Through([method.Method]).TotalAllocBytes, method.InclusiveBytes));
        Assert.Equal(1650, profile.TotalAllocBytes);
        Assert.Equal(470, profile.TotalSurvivedBytes);
        Assert.Equal(5, profile.Sites.Count);
    }

    [Fact]
    public void AQueryWithoutParametersNamesEveryOverload()
    {
        var profile = new AllocationProfile([
            new(Stack("App.Main", "App.Fill(int)"), "A", 100, 1, 0, 0),
            new(Stack("App.Main", "App.Fill(string)"), "A", 200, 1, 0, 0),
            new(Stack("App.Main", "App.Fill<T>(T)"), "A", 400, 1, 0, 0),
            new(Stack("App.Main", "App.Filler"), "A", 800, 1, 0, 0),
        ]);

        Assert.Equal(700, profile.Through(profile.MethodsMatching("App.Fill")).TotalAllocBytes); // not App.Filler
        Assert.Equal(200, profile.Through([Assert.Single(profile.MethodsMatching("App.Fill(string)"))]).TotalAllocBytes);
        Assert.Empty(profile.MethodsMatching("App.Fil"));

        AllocationTreeNode callers = AllocationTreeNode.BuildCallers(profile, profile.MethodsMatching("App.Fill"));
        Assert.Null(callers.Frame);
        Assert.Equal(700, callers.AllocBytes);
        Assert.Equal(Method("App.Main"), Assert.Single(callers.Children).Frame);
    }

    [Fact]
    public void SameNamedMethodsInDifferentAssembliesStayApartAndOneAssemblyLoadedTwiceMerges()
    {
        MethodFrame libA = Method("App.Mapper.Map(int)", "LibA.dll");
        MethodFrame libB = Method("App.Mapper.Map(int)", "LibB.dll");
        var profile = new AllocationProfile([
            new([libA], "A", 100, 1, 0, 0),
            new([libB], "A", 200, 1, 0, 0),
            new([Method("App.Mapper.Map(int)", "LibA.dll")], "A", 300, 1, 0, 0),
        ]);

        Assert.Equal([
            new AllocationMethodStat(libA, 400, 400, 2),
            new AllocationMethodStat(libB, 200, 200, 1),
        ], profile.HotMethods());
        Assert.Equal(2, profile.MethodsMatching("Mapper.Map").Count);
        Assert.Equal(400, profile.Through([libA]).TotalAllocBytes);
    }

    [Fact]
    public void ALabelSelectsOnlyTheMethodItShows()
    {
        MethodFrame first = Method("App.Mapper.Map(int)", "Lib.dll");
        MethodFrame second = new(Guid.NewGuid(), "Lib.dll", first.Token, first.Namespace, first.Type, first.Name, "",
            first.Parameters, first.QualifiedParameters, first.ReturnType, first.QualifiedReturnType, null);
        MethodLabels.Assign([first, second]);
        var profile = new AllocationProfile([
            new([first], "A", 100, 1, 0, 0),
            new([second], "A", 900, 1, 0, 0),
        ]);

        Assert.Equal(("Mapper.Map(int)", "Mapper.Map(int) #2"), (first.Label, second.Label));
        Assert.Equal([first], profile.MethodsMatching("Mapper.Map(int)"));
        Assert.Equal([second], profile.MethodsMatching("Mapper.Map(int) #2"));
        Assert.Equal(2, profile.MethodsMatching("Mapper.Map").Count);
    }

    [Fact]
    public void CallTreesKeepSameNamedMethodsApart()
    {
        MethodFrame libA = Method("App.Mapper.Map(int)", "LibA.dll");
        MethodFrame libB = Method("App.Mapper.Map(int)", "LibB.dll");
        var profile = new AllocationProfile([
            new([Method("App.Main"), libA], "A", 100, 1, 0, 0),
            new([Method("App.Main"), libB], "A", 200, 1, 0, 0),
        ]);

        AllocationTreeNode main = Assert.Single(AllocationTreeNode.Build(profile).Children);
        Assert.Equal([libB, libA], main.Children.Select(child => child.Frame));
    }

    [Fact]
    public void HotMethods_UsesOnlySitesInTheTypeProjection()
    {
        AllocationProfile profile = Profile();

        Assert.Equal([
            new AllocationMethodStat(Method("Other"), 400, 400, 4),
            new AllocationMethodStat(Method("Allocate"), 100, 500, 2),
        ], profile.OfType("App.First").HotMethods());
        Assert.Equal(new AllocationMethodStat(Method("Allocate"), 200, 200, 3),
            Assert.Single(profile.OfType("App.Second").HotMethods()));
        Assert.Empty(profile.OfType("App.Missing").HotMethods());
    }

    [Fact]
    public void HotMethods_CountsEveryTypeRecordForTheSameStack()
    {
        var profile = new AllocationProfile([
            new(Stack("Root", "Allocate"), "App.First", 100, 1, 0, 0),
            new(Stack("Root", "Allocate"), "App.Second", 200, 2, 0, 0),
            new(Stack("Root", "Allocate"), "App.First", 300, 3, 0, 0),
        ]);

        Assert.Equal(new AllocationMethodStat(Method("Allocate"), 600, 600, 6), Assert.Single(profile.HotMethods()));
        Assert.Equal(new AllocationMethodStat(Method("Allocate"), 400, 400, 4),
            Assert.Single(profile.OfType("App.First").HotMethods()));
    }

    [Fact]
    public void ByType_GroupsSitesByAllocatedTypeLargestFirst()
    {
        AllocationProfile profile = Profile();

        Assert.Equal([
            new AllocationTypeStat("App.First", 1400, 15, 420, 3),
            new AllocationTypeStat("App.Second", 200, 3, 50, 1),
            new AllocationTypeStat("App.Third", 50, 1, 0, 1),
        ], profile.ByType());
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
            new(Method("Other"), 400, 600, 4),
            new(Method("Allocate"), 350, 750, 6),
        ];

        Assert.Equal(expected.Take(limit), profile.HotMethods(limit));
        Assert.Equal(1650, profile.TotalAllocBytes);
    }

    [Fact]
    public void HotMethods_PreservesFirstLeafOrderForEqualSelfBytes()
    {
        var profile = new AllocationProfile([
            new(Stack("TieA", "TieB"), "A", 10, 1, 0, 0),
            new(Stack("TieB", "TieA"), "A", 10, 1, 0, 0),
            new(Stack("TieA", "Largest"), "A", 20, 1, 0, 0),
        ]);

        Assert.Equal(["Largest", "TieB", "TieA"], profile.HotMethods().Select(method => method.Method.Name));
        Assert.Equal(["Largest", "TieB"], profile.HotMethods(2).Select(method => method.Method.Name));
    }

    [Fact]
    public void HotMethods_KeepsMethodsWhoseNamesDifferOnlyInCaseApartAndLongTotals()
    {
        var profile = new AllocationProfile([
            new(Stack("Allocate"), "A", 3_000_000_000, 3_000_000_000, 0, 0),
            new(Stack("Allocate"), "A", 4_000_000_000, 4_000_000_000, 0, 0),
            new(Stack("allocate"), "A", 0, 0, 0, 0),
        ]);

        Assert.Equal([
            new AllocationMethodStat(Method("Allocate"), 7_000_000_000, 7_000_000_000, 7_000_000_000),
            new AllocationMethodStat(Method("allocate"), 0, 0, 0),
        ], profile.HotMethods());
    }

    [Fact]
    public void HotMethods_ReturnsEmptyForProfilesWithoutManagedFrames()
    {
        Assert.Empty(new AllocationProfile([]).HotMethods());
        Assert.Empty(new AllocationProfile([new([], "A", 100, 1, 0, 0)]).HotMethods());
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
