using System.Collections.Generic;
using System.Linq;
using Sherlock.Core.Collection;
using Xunit;

namespace Sherlock.Core.Tests.Collection;

public sealed class ProcessLocatorTests
{
    [Fact]
    public void DescendantsKeepParentLinksAndExcludeUnrelatedProcesses()
    {
        var children = new Dictionary<int, List<int>>
        {
            [1] = [10, 50],
            [10] = [20, 30],
            [20] = [40],
            [50] = [60],
        };

        Assert.Equal(
            new[] { (10, 0), (20, 10), (30, 10), (40, 20) },
            ProcessLocator.Descendants(10, children).ToArray());
    }

    [Fact]
    public void DescendantsVisitEachProcessOnceEvenWithCyclesAndDuplicates()
    {
        var children = new Dictionary<int, List<int>>
        {
            [10] = [20, 20],
            [20] = [10, 30],
            [30] = [20],
        };

        Assert.Equal(
            new[] { (10, 0), (20, 10), (30, 20) },
            ProcessLocator.Descendants(10, children).ToArray());
    }

    [Fact]
    public void FailedDiscoveryStillIncludesTheLaunchedRoot()
    {
        Assert.Equal(
            new[] { (10, 0) },
            ProcessLocator.Descendants(10, new Dictionary<int, List<int>>()).ToArray());
    }
}
