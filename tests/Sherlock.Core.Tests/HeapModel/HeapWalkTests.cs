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
    public void ExceptionsMatchClrMDsWalk()
    {
        using Snapshot snapshot = dump.Open();
        ulong[] expected = [.. snapshot.Runtime.Heap.EnumerateObjects().Where(o => o.IsException).Select(o => o.Address).Order()];

        ulong[] found = HeapWalk.Select(snapshot.GetDumpHeap(), type => type.IsException, static (_, _, _) => true, CancellationToken.None);

        Assert.True(expected.Length >= 2, "the target keeps two caught exceptions");
        Assert.Equal(expected, found);
        Assert.Equal(2, snapshot.GetExceptions().Count(e => e.TypeName == "System.InvalidOperationException"));
    }

    [Fact]
    public void EventLeaksMatchClrMDsWalk()
    {
        using Snapshot snapshot = dump.Open();
        var expected = new List<(ulong, int)>();
        foreach (ClrObject obj in snapshot.Runtime.Heap.EnumerateObjects())
        {
            if (obj.Type?.BaseType?.Name == "System.MulticastDelegate" && obj.ReadObjectField("_invocationList") is { IsValid: true, IsArray: true } list)
            {
                ClrArray invocation = list.AsArray();
                int subscribers = Enumerable.Range(0, invocation.Length).Count(i => invocation.GetObjectValue(i).IsValid);
                if (subscribers >= 16)
                {
                    expected.Add((obj.Address, subscribers));
                }
            }
        }

        IReadOnlyList<EventSubscription> found = snapshot.EventHandlerLeaks();

        Assert.Equal(expected.OrderByDescending(e => e.Item2).ThenBy(e => e.Item1), found.Select(e => (e.DelegateAddress, e.SubscriberCount)));
        Assert.Equal([60, 40, 20], found.Select(e => e.SubscriberCount).Where(count => count is 60 or 40 or 20));
        EventSubscription largest = found.First(e => e.SubscriberCount == 60);
        Assert.Contains(("Node", 30), largest.Targets.Select(t => (t.TypeName, t.Count)));
        Assert.Equal(60, largest.Targets.Sum(t => t.Count));
    }

    [Fact]
    public void DuplicateStringsMatchGroupingEveryValue()
    {
        using Snapshot snapshot = dump.Open();
        var groups = new Dictionary<string, (long Count, ulong Size, ulong First)>(StringComparer.Ordinal);
        foreach (ClrObject obj in snapshot.Runtime.Heap.EnumerateObjects().Where(o => o.Type?.IsString == true))
        {
            string value = obj.AsString(int.MaxValue)!;
            (long count, ulong size, ulong first) = groups.GetValueOrDefault(value, (0, obj.Size, ulong.MaxValue));
            groups[value] = (count + 1, size, Math.Min(first, obj.Address));
        }
        var expected = groups.Where(g => g.Value.Count > 1)
            .Select(g => (Value: g.Key.Length > 65536 ? g.Key[..65536] : g.Key, g.Value.Count, Total: (ulong)g.Value.Count * g.Value.Size, Wasted: (ulong)(g.Value.Count - 1) * g.Value.Size, g.Value.First))
            .OrderByDescending(g => g.Wasted).ThenBy(g => g.First).Take(50).ToList();

        IReadOnlyList<DuplicateString> found = snapshot.DuplicateStrings(50);

        Assert.Contains(found, d => d.Value == new string('x', 40) && d.Count == 3_000);
        Assert.Contains(found, d => d.Value.Length == 65536 && d.Count == 2);
        Assert.Equal(expected.Select(g => (g.Value, g.Count, g.Total, g.Wasted)), found.Select(d => (d.Value, d.Count, d.TotalSize, d.WastedBytes)));
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
