using System;
using System.IO;
using System.Runtime.InteropServices;
using Sherlock.Core.Profiling;
using Sherlock.Core.Storage;
using Sherlock.Core.Tests.Common;
using Xunit;

namespace Sherlock.Core.Tests.Profiling;

public class ProvenanceTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    // Serializes a provenance writer to a temp .slab and opens it (exercises the real mmap + Column path).
    private SlabFile Write(ProvenanceWriter w)
    {
        var cw = new ContainerWriter();
        w.WriteTo(cw);
        return _tmp.WriteSlab(cw);
    }

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
        uint t1 = w.InternType("MyApp.Customer");
        uint t2 = w.InternType("System.Byte[]");
        w.AddAllocation(s1, t1, allocBytes: 2000, allocCount: 50, survivedBytes: 1600, survivedCount: 40);
        w.AddAllocation(s2, t2, allocBytes: 512, allocCount: 8, survivedBytes: 0, survivedCount: 0);

        using SlabFile slab = Write(w);
        var r = new ProvenanceReader(slab);

        Column<AllocationRecord> recs = r.Allocations;
        Assert.Equal(2L, recs.Length);

        Assert.Equal(s1, recs[0].StackId);
        Assert.Equal(2000ul, recs[0].AllocBytes);
        Assert.Equal(50ul, recs[0].AllocCount);
        Assert.Equal(1600ul, recs[0].SurvivedBytes);
        Assert.Equal(40ul, recs[0].SurvivedCount);
        Assert.Equal(512ul, recs[1].AllocBytes);
        Assert.Equal("MyApp.Customer", r.Stacks.Frame(recs[0].TypeId));
        Assert.Equal("System.Byte[]", r.Stacks.Frame(recs[1].TypeId));

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

        using SlabFile slab = Write(w);
        var r = new ProvenanceReader(slab);

        Assert.Equal(3L, r.CorrelationCount);

        Assert.True(r.TryGetStack(0x2000, out uint sid));
        Assert.Equal(s1, sid);
        Assert.Equal("Program.Main;Registry.Add", r.StackFor(0x2000));
        Assert.Equal("Program.Main;List.Resize", r.StackFor(0x3000));
        Assert.Equal("Program.Main;Registry.Add", r.StackFor(0x1000));
        Assert.False(r.TryGetStack(0x1500, out _)); // untracked
        Assert.Null(r.StackFor(0x1500));
    }

    [Fact]
    public void NoCorrelation_WhenAggregateOnly()
    {
        var w = new ProvenanceWriter();
        w.AddAllocation(w.InternStack(["A"]), w.InternType("T"), 100, 1, 100, 1);
        using SlabFile slab = Write(w); // no AddObject → no Correlation section
        var r = new ProvenanceReader(slab);
        Assert.Equal(0L, r.CorrelationCount);
        Assert.Null(r.StackFor(0x1000));
    }

    [Fact]
    public void RejectsMissingStackTable()
    {
        var writer = new ContainerWriter();
        writer.AddRecords(SectionType.Allocations, ProfileFormat.Version, new AllocationRecord[1]);
        using SlabFile slab = _tmp.WriteSlab(writer);

        Assert.Throws<InvalidDataException>(() => new ProvenanceReader(slab));
    }

    [Fact]
    public void RejectsUnknownAllocationStack()
    {
        var writer = new ProvenanceWriter();
        writer.AddAllocation(stackId: 42, typeId: writer.InternType("T"), allocBytes: 100, allocCount: 1, survivedBytes: 0, survivedCount: 0);
        using SlabFile slab = Write(writer);

        Assert.Throws<InvalidDataException>(() => new ProvenanceReader(slab));
    }

    [Fact]
    public void RejectsUnknownCorrelationStack()
    {
        var writer = new ProvenanceWriter();
        writer.InternStack(["A"]);
        writer.AddObject(0x1000, stackId: 42);
        using SlabFile slab = Write(writer);

        Assert.Throws<InvalidDataException>(() => new ProvenanceReader(slab));
    }
}
