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

    [Fact]
    public void CancelledWalkStops()
    {
        using Snapshot snapshot = dump.Open();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => HeapWalk.Histogram(snapshot.GetDumpHeap(), cancelled.Token));
    }
}
