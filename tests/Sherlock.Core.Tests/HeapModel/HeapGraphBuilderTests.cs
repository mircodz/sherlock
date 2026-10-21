using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.Tests.Common;
using Sherlock.Heap;
using Xunit;

namespace Sherlock.Core.Tests.HeapModel;

[Collection(TargetDumpCollection.Name)]
public sealed class HeapGraphBuilderTests(TargetDump dump) : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void GraphHoldsClrMDsObjectsReferencesAndRoots()
    {
        using HeapDump heapDump = HeapDump.Open(dump.Path, _tmp.Path);
        string path = Path.Combine(_tmp.Path, "heapgraph.slab");
        // Small chunks split segments, so the second walk starts mid-segment many times.
        HeapGraphBuilder.Build(heapDump.GetDumpHeap(), heapDump.DumpPath, path, chunkBytes: 64 << 10, CancellationToken.None);
        using HeapGraph graph = HeapGraphStore.Load(path, heapDump.DumpPath)!;

        ClrHeap heap = heapDump.Runtime.Heap;
        ClrObject[] all = [.. heap.EnumerateObjects().OrderBy(o => o.Address)];
        ClrObject[] objects = [.. all.Where(o => !o.IsFree)];
        Assert.Equal(objects.Select(o => o.Address), graph.Addresses.ToArray());
        ulong[] addresses = graph.Addresses.ToArray();
        for (int i = 0; i < objects.Length; i++)
        {
            Assert.Equal(objects[i].Size, graph.SizeOf(i));
            Assert.Equal(objects[i].Type!.Name, graph.TypeNameOf(i));
            ulong[] references = [.. objects[i].EnumerateReferenceAddresses(carefully: false, considerDependantHandles: false)];
            Assert.Equal(references, graph.Successors(i).ToArray().Select(id => addresses[id]));
        }
        Assert.Equal(all.Count(o => o.IsFree), graph.FreeCount);
        Assert.Equal(all.Where(o => o.IsFree).Aggregate(0UL, (sum, o) => sum + o.Size), graph.FreeBytes);

        ClrRoot[] roots = [.. heap.EnumerateRoots().Where(r => r.Object.Address != 0)];
        HeapRootRecord[] records = graph.Roots.ToArray();
        Assert.Equal(roots.Length, records.Length);
        for (int i = 0; i < roots.Length; i++)
        {
            Assert.Equal(roots[i].Object.Address, addresses[records[i].ObjectId]);
            Assert.Equal((roots[i].Address, roots[i].RootKind, roots[i].IsInterior, roots[i].IsPinned),
                (records[i].Address, records[i].Kind, records[i].IsInterior, records[i].IsPinned));
        }
        Assert.Equal(records.Select(r => r.ObjectId).Distinct(), graph.Successors(graph.Root).ToArray());
    }

    [Fact]
    public void HeapDumpBuildsTheGraphOnceAndReopensIt()
    {
        Guid built;
        using (HeapDump first = HeapDump.Open(dump.Path, _tmp.Path))
        {
            built = first.GetHeapGraph().Id;
        }
        using HeapDump second = HeapDump.Open(dump.Path, _tmp.Path);

        Assert.Equal(built, second.GetHeapGraph().Id);
        Assert.Equal(["heapgraph.slab"], Directory.GetFiles(_tmp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void CancelledBuildLeavesNothingBehind()
    {
        using HeapDump heapDump = HeapDump.Open(dump.Path, _tmp.Path);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            HeapGraphBuilder.Build(heapDump.GetDumpHeap(), heapDump.DumpPath, Path.Combine(_tmp.Path, "heapgraph.slab"), cancelled.Token));
        Assert.Empty(Directory.GetFiles(_tmp.Path));
    }
}
