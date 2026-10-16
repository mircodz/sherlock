using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Sherlock.Core.Profiling;
using Sherlock.Core.Storage;
using Sherlock.Core.Tests.Common;
using Xunit;
using static Sherlock.Tests.Frames;

namespace Sherlock.Core.Tests.Profiling;

public class ProvenanceTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private SlabFile Write(ProvenanceWriter w)
    {
        var cw = new ContainerWriter(SlabKind.Profile, ProfileFormat.Version);
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
        uint s1 = w.InternStack(Stack("Program.Main", "Registry.Add"));
        uint s2 = w.InternStack(Stack("Program.Main", "List.Resize"));
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
        Assert.Equal("MyApp.Customer", r.Symbols.Types[(int)recs[0].TypeId]);
        Assert.Equal("System.Byte[]", r.Symbols.Types[(int)recs[1].TypeId]);

        Assert.Equal(Stack("Program.Main", "Registry.Add"), r.Symbols.Stack(recs[0].StackId));
        Assert.Equal(Stack("Program.Main", "List.Resize"), r.Symbols.Stack(recs[1].StackId));
        Assert.Equal(["Program.Main()", "Registry.Add()", "List.Resize()"], r.Symbols.Methods.Select(method => method.Label));
    }

    [Fact]
    public void SharesOneStackAcrossSites()
    {
        var w = new ProvenanceWriter();
        Assert.Equal(w.InternStack(Stack("A", "B")), w.InternStack(Stack("A", "B")));
        Assert.NotEqual(w.InternStack(Stack("A", "B")), w.InternStack(Stack("B", "A")));
    }

    [Fact]
    public void LiveObjectsAreTwoColumnsOfTwelveBytesEach()
    {
        var w = new ProvenanceWriter();
        uint stack = w.InternStack(Stack("A"));
        w.AddObject(0x2000, stack);
        w.AddObject(0x1000, stack);
        using SlabFile slab = Write(w);

        Assert.Equal(new ulong[] { 0x1000, 0x2000 }, slab.GetColumn<ulong>(SectionType.ObjectAddresses).AsMemory().ToArray());
        Assert.Equal(new uint[] { stack, stack }, slab.GetColumn<uint>(SectionType.ObjectStacks).AsMemory().ToArray());
    }

    [Fact]
    public void CorrelationIsSortedAndBinarySearchable()
    {
        var w = new ProvenanceWriter();
        uint s1 = w.InternStack(Stack("Program.Main", "Registry.Add"));
        uint s2 = w.InternStack(Stack("Program.Main", "List.Resize"));
        w.AddObject(0x3000, s2); // inserted out of address order
        w.AddObject(0x1000, s1);
        w.AddObject(0x2000, s1);

        using SlabFile slab = Write(w);
        var r = new ProvenanceReader(slab);

        Assert.Equal(3L, r.CorrelationCount);

        Assert.True(r.TryGetStack(0x2000, out uint sid));
        Assert.Equal(s1, sid);
        Assert.Equal(Stack("Program.Main", "Registry.Add"), r.FramesFor(0x2000)!);
        Assert.Equal(Stack("Program.Main", "List.Resize"), r.FramesFor(0x3000)!);
        Assert.Equal(Stack("Program.Main", "Registry.Add"), r.FramesFor(0x1000)!);
        Assert.False(r.TryGetStack(0x1500, out _)); // untracked
        Assert.Null(r.FramesFor(0x1500));
    }

    [Fact]
    public void RuntimeAllocationsHaveAnEmptyStackRatherThanNone()
    {
        var w = new ProvenanceWriter();
        uint empty = w.InternStack([]);
        w.AddAllocation(empty, w.InternType("T"), 100, 1, 100, 1);
        w.AddObject(0x1000, empty);
        using SlabFile slab = Write(w);
        var r = new ProvenanceReader(slab);

        Assert.Empty(r.FramesFor(0x1000)!);
        Assert.Null(r.FramesFor(0x2000));
    }

    [Fact]
    public void NoCorrelation_WhenAggregateOnly()
    {
        var w = new ProvenanceWriter();
        w.AddAllocation(w.InternStack(Stack("A")), w.InternType("T"), 100, 1, 100, 1);
        using SlabFile slab = Write(w); // no AddObject → no Correlation section
        var r = new ProvenanceReader(slab);
        Assert.Equal(0L, r.CorrelationCount);
        Assert.Null(r.FramesFor(0x1000));
    }

    [Fact]
    public void RejectsUnknownAllocationType()
    {
        var writer = new ProvenanceWriter();
        writer.AddAllocation(writer.InternStack(Stack("A")), typeId: 7, allocBytes: 100, allocCount: 1, survivedBytes: 0, survivedCount: 0);
        using SlabFile slab = Write(writer);

        Assert.Throws<InvalidDataException>(() => new ProvenanceReader(slab));
    }

    [Theory]
    [InlineData(ContainerFormat.Version, ProfileFormat.Version + 1)]
    [InlineData(1, ProfileFormat.Version)]
    public void RejectsProfilesFromOtherVersionsWithWhatToDo(ushort container, uint profile)
    {
        var writer = new ContainerWriter(SlabKind.Profile, profile);
        new ProvenanceWriter().WriteTo(writer);
        byte[] bytes = writer.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), container);
        string path = _tmp.File();
        File.WriteAllBytes(path, bytes);

        var error = Assert.Throws<InvalidDataException>(() => ProvenanceReader.ValidateFile(path));

        Assert.Contains("record it again", error.Message);
    }

    [Fact]
    public void RejectsAFileOfAnotherKind()
    {
        string path = _tmp.File();
        new ContainerWriter(SlabKind.Graph, 1).Save(path);

        Assert.Contains("holds a heap graph, not an allocation profile",
            Assert.Throws<InvalidDataException>(() => ProvenanceReader.ValidateFile(path)).Message);
    }

    [Theory]
    [InlineData(ulong.MaxValue, 0ul)]
    [InlineData((ulong)long.MaxValue, 1ul)]
    public void RejectsAllocationTotalsTooLargeToCount(ulong first, ulong second)
    {
        var writer = new ProvenanceWriter();
        uint stack = writer.InternStack(Stack("A"));
        uint type = writer.InternType("T");
        writer.AddAllocation(stack, type, first, 1, 0, 0);
        writer.AddAllocation(stack, type, second, 1, 0, 0);
        using SlabFile slab = Write(writer);

        Assert.Contains("too large", Assert.Throws<InvalidDataException>(() => new ProvenanceReader(slab)).Message);
    }

    [Fact]
    public void RejectsMissingStackTable()
    {
        var writer = new ContainerWriter(SlabKind.Profile, ProfileFormat.Version);
        writer.AddRecords(SectionType.Allocations, new AllocationRecord[1]);
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
        writer.InternStack(Stack("A"));
        writer.AddObject(0x1000, stackId: 42);
        using SlabFile slab = Write(writer);

        Assert.Throws<InvalidDataException>(() => new ProvenanceReader(slab));
    }
}
