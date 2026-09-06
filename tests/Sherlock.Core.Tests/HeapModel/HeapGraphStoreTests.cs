using System;
using System.IO;
using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.HeapModel;
using Sherlock.Core.Tests.Common;
using Xunit;

namespace Sherlock.Core.Tests.HeapModel;

public sealed class HeapGraphStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _path;

    public HeapGraphStoreTests() => _path = _tmp.File();

    public void Dispose() => _tmp.Dispose();

    // Diamond: root -> 0; 0 -> 1,2; 1 -> 3; 2 -> 3.
    private static HeapGraph SampleGraph()
    {
        ulong[] addresses = [0x1000, 0x2000, 0x3000, 0x4000];
        uint[] sizes = [16, 24, 24, 48];
        int[] offsets = [0, 2, 3, 4, 4, 5];
        int[] edges = [1, 2, 3, 3, 0];
        return new HeapGraph(addresses, sizes, offsets, edges);
    }

    [Fact]
    public void RoundTrips_StructurallyIdentical()
    {
        HeapGraph original = SampleGraph();
        HeapGraphStore.Save(_path, original);
        HeapGraph? loaded = HeapGraphStore.Load(_path);

        Assert.NotNull(loaded);
        Assert.Equal(original.Addresses.ToArray(), loaded!.Addresses.ToArray());
        Assert.Equal(original.Sizes.ToArray(), loaded.Sizes.ToArray());
        Assert.Equal(original.Offsets.ToArray(), loaded.Offsets.ToArray());
        Assert.Equal(original.Edges.ToArray(), loaded.Edges.ToArray());
        Assert.Equal(original.ObjectCount, loaded.ObjectCount);
        Assert.Equal(original.Root, loaded.Root);
        Assert.Equal(original.NodeCount, loaded.NodeCount);
    }

    [Fact]
    public void RoundTrips_SuccessorsPreserved()
    {
        HeapGraphStore.Save(_path, SampleGraph());
        HeapGraph loaded = HeapGraphStore.Load(_path)!;

        Assert.Equal([1, 2], loaded.Successors(0).ToArray());
        Assert.Equal([3], loaded.Successors(1).ToArray());
        Assert.Equal([3], loaded.Successors(2).ToArray());
        Assert.Equal([], loaded.Successors(3).ToArray());
        Assert.Equal([0], loaded.Successors(loaded.Root).ToArray()); // synthetic root -> GC roots
    }

    [Fact]
    public void RoundTripsEveryRootRecord()
    {
        HeapGraph graph = SampleGraph();
        HeapRootRecord[] roots =
        [
            new(0, 0xabc, ClrRootKind.Stack, true, false),
            new(0, 0xdef, ClrRootKind.PinnedHandle, false, true),
        ];
        var original = new HeapGraph(graph.Addresses, graph.Sizes, graph.Offsets, graph.Edges,
            null, null, 0, 0, roots);

        HeapGraphStore.Save(_path, original);
        using HeapGraph loaded = HeapGraphStore.Load(_path)!;

        Assert.Equal(2, loaded.Roots.Length);
        Assert.Equal(ClrRootKind.Stack, loaded.Roots.Span[0].Kind);
        Assert.True(loaded.Roots.Span[0].IsInterior);
        Assert.Equal(0xdefUL, loaded.Roots.Span[1].Address);
        Assert.True(loaded.Roots.Span[1].IsPinned);
    }

    [Fact]
    public void RoundTrips_MultiChunkEdges_SplitAndReassemble()
    {
        // A tiny budget exercises multi-chunk persistence without allocating billions of edges.
        ulong[] addresses = [0x1000, 0x2000, 0x3000, 0x4000];
        uint[] sizes = [16, 24, 24, 48];
        long[] offsets = [0, 2, 3, 4, 4, 5]; // same CSR as SampleGraph, as long
        int[] flatEdges = [1, 2, 3, 3, 0];
        var col = EdgeColumn.Build([new ReadOnlyMemory<int>(flatEdges)], offsets, maxEdgesPerChunk: 2);
        Assert.True(col.ChunkCount > 1, "test setup should produce multiple chunks");

        var original = new HeapGraph(addresses, sizes, offsets, col);
        HeapGraphStore.Save(_path, original);
        using HeapGraph loaded = HeapGraphStore.Load(_path)!;

        Assert.True(loaded.Edges.ChunkCount > 1);
        Assert.Equal(5, loaded.Edges.Count);
        Assert.Equal([1, 2], loaded.Successors(0).ToArray());
        Assert.Equal([3], loaded.Successors(1).ToArray());
        Assert.Equal([3], loaded.Successors(2).ToArray());
        Assert.Equal([], loaded.Successors(3).ToArray());
        Assert.Equal([0], loaded.Successors(loaded.Root).ToArray());
    }

    [Fact]
    public void RoundTrips_AddressLookupPreserved()
    {
        HeapGraphStore.Save(_path, SampleGraph());
        HeapGraph loaded = HeapGraphStore.Load(_path)!;

        Assert.Equal(0, loaded.IndexOf(0x1000));
        Assert.Equal(3, loaded.IndexOf(0x4000));
        Assert.Equal(-1, loaded.IndexOf(0x1500)); // between objects
        Assert.Equal(-1, loaded.IndexOf(0x9999)); // past the end
    }

    [Fact]
    public void LoadedGraph_IsMmapBacked_StaysValidUntilDisposed()
    {
        // Edge chunks borrow the slab mapping; the graph owns its lifetime.
        HeapGraphStore.Save(_path, SampleGraphWithTypes());
        HeapGraph loaded = HeapGraphStore.Load(_path)!;

        Assert.Equal([0x1000, 0x2000, 0x3000, 0x4000], loaded.Addresses.ToArray());
        Assert.Equal([1, 2], loaded.Successors(0).ToArray());
        Assert.Equal("System.String", loaded.TypeNameOf(0));

        loaded.Dispose();
        Assert.Throws<ObjectDisposedException>(() => loaded.Successors(0));
        File.Delete(_path); // mapping released, so the file is deletable
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void EmptyGraph_RoundTrips()
    {
        // No objects: Offsets is [root_start, end] = [0, 0], no edges.
        var empty = new HeapGraph((ulong[])[], (uint[])[], (int[])[0, 0], (int[])[]);
        HeapGraphStore.Save(_path, empty);
        HeapGraph loaded = HeapGraphStore.Load(_path)!;

        Assert.Equal(0, loaded.ObjectCount);
        Assert.True(loaded.Addresses.IsEmpty);
        Assert.Equal(0, loaded.Edges.Count);
    }

    [Fact]
    public void MissingFile_ReturnsNullOrThrows()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():n}.slab");
        Assert.ThrowsAny<Exception>(() => HeapGraphStore.Load(missing));
    }

    // A graph with the optional type column: object type ids + a name table.
    private static HeapGraph SampleGraphWithTypes()
    {
        ulong[] addresses = [0x1000, 0x2000, 0x3000, 0x4000];
        uint[] sizes = [16, 24, 24, 48];
        int[] offsets = [0, 2, 3, 4, 4, 5];
        int[] edges = [1, 2, 3, 3, 0];
        int[] typeIds = [0, 1, 1, 2];               // obj0=String, obj1/2=Node, obj3=Byte[]
        string[] typeNames = ["System.String", "MyApp.Node", "System.Byte[]"];
        return new HeapGraph(addresses, sizes, offsets, edges, typeIds, typeNames);
    }

    [Fact]
    public void RoundTrips_TypeColumnPreserved()
    {
        HeapGraphStore.Save(_path, SampleGraphWithTypes());
        HeapGraph loaded = HeapGraphStore.Load(_path)!;

        Assert.True(loaded.HasTypes);
        Assert.Equal([0, 1, 1, 2], loaded.TypeIds!.Value.ToArray());
        Assert.NotNull(loaded.TypeNames);
        Assert.Equal(["System.String", "MyApp.Node", "System.Byte[]"], loaded.TypeNames);
        Assert.Equal("System.String", loaded.TypeNameOf(0));
        Assert.Equal("MyApp.Node", loaded.TypeNameOf(1));
        Assert.Equal("System.Byte[]", loaded.TypeNameOf(3));
    }

    [Fact]
    public void GraphWithoutTypes_LoadsWithoutTypes()
    {
        // Missing type columns require the ClrMD fallback.
        HeapGraphStore.Save(_path, SampleGraph());
        HeapGraph loaded = HeapGraphStore.Load(_path)!;

        Assert.False(loaded.HasTypes);
        Assert.Null(loaded.TypeIds);
        Assert.Null(loaded.TypeNameOf(0));
    }

    [Fact]
    public void Histogram_FromTypeColumn_IsCorrect()
    {
        HeapGraph g = SampleGraphWithTypes();
        var hist = g.Histogram();

        Assert.NotNull(hist);
        var byName = System.Linq.Enumerable.ToDictionary(hist!, h => h.TypeName);
        Assert.Equal((1L, 16u), (byName["System.String"].Count, byName["System.String"].TotalSize));
        Assert.Equal((2L, 48u), (byName["MyApp.Node"].Count, byName["MyApp.Node"].TotalSize)); // 24+24
        Assert.Equal((1L, 48u), (byName["System.Byte[]"].Count, byName["System.Byte[]"].TotalSize));
    }

    [Fact]
    public void Histogram_NullWithoutTypes()
    {
        Assert.Null(SampleGraph().Histogram());
    }

    [Fact]
    public void FreeBytes_RoundTripAndSyntheticFreeRow()
    {
        // The doctor's fragmentation check relies on the synthetic Free row.
        ulong[] addresses = [0x1000, 0x2000];
        uint[] sizes = [16, 24];
        int[] offsets = [0, 0, 0, 0];
        int[] edges = [];
        var g = new HeapGraph(addresses, sizes, offsets, edges, (int[])[0, 0], ["MyApp.Node"],
            freeBytes: 4096, freeCount: 7);

        HeapGraphStore.Save(_path, g);
        HeapGraph loaded = HeapGraphStore.Load(_path)!;
        Assert.Equal(4096u, loaded.FreeBytes);
        Assert.Equal(7L, loaded.FreeCount);

        var free = System.Array.Find(loaded.Histogram()!, h => h.TypeName == "Free");
        Assert.Equal(7L, free.Count);
        Assert.Equal(4096u, free.TotalSize);
    }

    [Fact]
    public void Constructor_RejectsMismatchedTypeArrays()
    {
        ulong[] addr = [0x1000, 0x2000];
        uint[] sizes = [8, 8];
        int[] offsets = [0, 0, 0, 0];
        int[] edges = [];
        // typeIds present but typeNames absent → invalid.
        Assert.Throws<ArgumentException>(() => new HeapGraph(addr, sizes, offsets, edges, (int[])[0, 0], null));
        // typeIds length must equal object count.
        Assert.Throws<ArgumentException>(() => new HeapGraph(addr, sizes, offsets, edges, (int[])[0], ["T"]));
    }

    [Fact]
    public void LoadRejectsCacheFromAChangedDump()
    {
        string dump = _tmp.File(".dmp");
        File.WriteAllBytes(dump, [1, 2, 3]);
        HeapGraphStore.Save(_path, SampleGraph(), dump);
        Assert.NotNull(HeapGraphStore.Load(_path, dump));

        File.WriteAllBytes(dump, [1, 2, 3, 4]);

        Assert.Null(HeapGraphStore.Load(_path, dump));
    }
}
