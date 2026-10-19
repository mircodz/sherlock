using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.HeapModel;
using Xunit;

namespace Sherlock.Core.Tests.HeapModel;

[Collection(TargetDumpCollection.Name)]
public sealed class HeapWalkTests(TargetDump dump)
{
    [Fact]
    public void HistogramMatchesClrMDsWalk()
    {
        using Snapshot snapshot = dump.Open();
        var expected = new Dictionary<string, (long Count, ulong Bytes)>(StringComparer.Ordinal);
        foreach (ClrObject obj in snapshot.Runtime.Heap.EnumerateObjects())
        {
            if (obj.Type is not null)
            {
                (long count, ulong bytes) = expected.GetValueOrDefault(obj.Type.Name ?? "<unknown>");
                expected[obj.Type.Name ?? "<unknown>"] = (count + 1, bytes + obj.Size);
            }
        }

        IReadOnlyList<HeapTypeStat> actual = HeapWalk.Histogram(snapshot.GetDumpHeap(), CancellationToken.None);

        Assert.True(actual.Sum(row => row.Count) > 10_000, "the dump should hold a real heap");
        Assert.Equal(expected.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (e.Key, e.Value.Count, e.Value.Bytes)),
            actual.OrderBy(r => r.TypeName, StringComparer.Ordinal).Select(r => (r.TypeName, r.Count, r.TotalSize)));
    }

    [Theory]
    [InlineData("string", false)]
    [InlineData("Node", true)]
    public void InstancesMatchClrMDsWalk(string filter, bool exact)
    {
        using Snapshot snapshot = dump.Open();
        Func<string, bool> matches = name => exact ? name == filter : name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        ClrObject[] matching = [.. snapshot.Runtime.Heap.EnumerateObjects().Where(o => !o.IsFree && matches(o.Type!.Name ?? "<unknown>"))];

        InstanceListing listing = HeapWalk.Instances(snapshot.GetDumpHeap(), matches, 25, CancellationToken.None);

        Assert.True(matching.Length > 25, "the filter should match more objects than the limit");
        Assert.Equal(matching.Length, listing.TotalMatched);
        Assert.Equal(matching.Aggregate(0UL, (sum, o) => sum + o.Size), listing.TotalMatchedSize);
        Assert.Equal(matching.OrderByDescending(o => o.Size).ThenBy(o => o.Address).Take(25).Select(o => (o.Address, o.Size, o.Type!.Name)),
            listing.Instances.Select(i => (i.Address, i.Size, (string?)i.TypeName)));
    }

    [Fact]
    public void CancelledWalkStops()
    {
        using Snapshot snapshot = dump.Open();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => HeapWalk.Histogram(snapshot.GetDumpHeap(), cancelled.Token));
    }
}
