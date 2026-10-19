using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.HeapModel;
using Sherlock.Core.Tests.Common;
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
        using Snapshot snapshot = Snapshot.Open(dump.Path, null, _tmp.Path);
        string path = Path.Combine(_tmp.Path, "heapgraph.slab");
        // Small chunks split segments, so the second walk starts mid-segment many times.
        HeapGraphBuilder.Build(snapshot.GetDumpHeap(), snapshot.DumpPath, path, chunkBytes: 64 << 10, CancellationToken.None);
        using HeapGraph graph = HeapGraphStore.Load(path, snapshot.DumpPath)!;

        ClrHeap heap = snapshot.Runtime.Heap;
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
    public void SnapshotBuildsTheGraphOnceAndReopensIt()
    {
        Guid built;
        using (Snapshot first = Snapshot.Open(dump.Path, null, _tmp.Path))
        {
            built = first.GetHeapGraph().Id;
        }
        using Snapshot second = Snapshot.Open(dump.Path, null, _tmp.Path);

        Assert.Equal(built, second.GetHeapGraph().Id);
        Assert.Equal(["heapgraph.slab"], Directory.GetFiles(_tmp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void CancelledBuildLeavesNothingBehind()
    {
        using Snapshot snapshot = Snapshot.Open(dump.Path, null, _tmp.Path);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            HeapGraphBuilder.Build(snapshot.GetDumpHeap(), snapshot.DumpPath, Path.Combine(_tmp.Path, "heapgraph.slab"), cancelled.Token));
        Assert.Empty(Directory.GetFiles(_tmp.Path));
    }
}
