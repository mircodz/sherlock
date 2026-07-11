using System.Runtime.InteropServices;
using Sherlock.Core.Storage;
using Xunit;

namespace Sherlock.Core.Tests.Storage;

public class ProvenanceTests
{
    [Fact]
    public void AllocationRecordLayoutMatchesNative()
    {
        // Native AllocationRecord is a packed, naturally-aligned 40-byte record.
        Assert.Equal(40, Marshal.SizeOf<AllocationRecord>());
    }

    [Fact]
    public void RoundTripsAllocationsAndStacks()
    {
        var w = new ProvenanceWriter();
        uint s1 = w.InternStack(["Program.Main", "Registry.Add"]);
        uint s2 = w.InternStack(["Program.Main", "List.Resize"]);
        w.AddAllocation(s1, allocBytes: 2000, allocCount: 50, survivedBytes: 1600, survivedCount: 40);
        w.AddAllocation(s2, allocBytes: 512, allocCount: 8, survivedBytes: 0, survivedCount: 0);

        var cw = new ContainerWriter();
        w.WriteTo(cw);
        var r = new ProvenanceReader(new ContainerReader(cw.ToArray()));

        var recs = r.Allocations;
        Assert.Equal(2, recs.Length);

        Assert.Equal(s1, recs[0].StackId);
        Assert.Equal(2000ul, recs[0].AllocBytes);
        Assert.Equal(50ul, recs[0].AllocCount);
        Assert.Equal(1600ul, recs[0].SurvivedBytes);
        Assert.Equal(40ul, recs[0].SurvivedCount);
        Assert.Equal(512ul, recs[1].AllocBytes);

        // stackId resolves back through the shared table.
        Assert.Equal("Program.Main;Registry.Add", r.Stacks.FormatStack(recs[0].StackId));
        Assert.Equal("Program.Main;List.Resize", r.Stacks.FormatStack(recs[1].StackId));
    }

    [Fact]
    public void SharesOneStackAcrossSites()
    {
        var w = new ProvenanceWriter();
        Assert.Equal(w.InternStack(["A", "B"]), w.InternStack(["A", "B"]));
    }

    [Fact]
    public void CorrelationRecordLayoutMatchesNative()
    {
        Assert.Equal(16, Marshal.SizeOf<CorrelationRecord>());
    }

    [Fact]
    public void CorrelationIsSortedAndBinarySearchable()
    {
        var w = new ProvenanceWriter();
        uint s1 = w.InternStack(["Program.Main", "Registry.Add"]);
        uint s2 = w.InternStack(["Program.Main", "List.Resize"]);
        w.AddObject(0x3000, s2); // inserted out of address order
        w.AddObject(0x1000, s1);
        w.AddObject(0x2000, s1);

        var cw = new ContainerWriter();
        w.WriteTo(cw);
        var r = new ProvenanceReader(new ContainerReader(cw.ToArray()));

        var corr = r.Correlation;
        Assert.Equal(3, corr.Length);
        Assert.Equal(0x1000ul, corr[0].Address); // sorted ascending
        Assert.Equal(0x2000ul, corr[1].Address);
        Assert.Equal(0x3000ul, corr[2].Address);

        Assert.True(r.TryGetStack(0x2000, out uint sid));
        Assert.Equal(s1, sid);
        Assert.Equal("Program.Main;Registry.Add", r.StackFor(0x2000));
        Assert.Equal("Program.Main;List.Resize", r.StackFor(0x3000));
        Assert.False(r.TryGetStack(0x1500, out _)); // untracked
        Assert.Null(r.StackFor(0x1500));
    }

    [Fact]
    public void NoCorrelation_WhenAggregateOnly()
    {
        var w = new ProvenanceWriter();
        w.AddAllocation(w.InternStack(["A"]), 100, 1, 100, 1);
        var cw = new ContainerWriter();
        w.WriteTo(cw); // no AddObject → no Correlation section
        var r = new ProvenanceReader(new ContainerReader(cw.ToArray()));
        Assert.True(r.Correlation.IsEmpty);
        Assert.Null(r.StackFor(0x1000));
    }
}
