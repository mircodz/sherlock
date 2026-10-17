using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sherlock.Core.HeapModel;
using Sherlock.Core.Tests.Common;
using Xunit;

namespace Sherlock.Core.Tests.HeapModel;

public sealed class HeapGraphProviderTests
{
    [Fact]
    public void ConcurrentCallersShareOneLoadedGraph()
    {
        using var tmp = new TempDir();
        string dump = Path.Combine(tmp.Path, "heap.dmp");
        File.WriteAllBytes(dump, [1, 2, 3]);
        ulong[] addresses = [0x1000, 0x2000];
        uint[] sizes = [24, 24];
        int[] offsets = [0, 1, 1, 2];
        int[] edges = [1, 0];
        using (var graph = new HeapGraph(addresses, sizes, offsets, edges))
        {
            HeapGraphStore.Save(Path.Combine(tmp.Path, "heapgraph.slab"), graph, dump);
        }
        using var provider = new HeapGraphProvider(SnapshotOf(dump, tmp.Path));

        HeapGraph?[] graphs = OnceTests.RunTogether(16, () => provider.TryGetCachedOrOnDisk());

        Assert.NotNull(graphs[0]);
        Assert.All(graphs, graph => Assert.Same(graphs[0], graph));
    }

    // Only the dump path and cache folder: loading a saved graph touches nothing else.
    private static Snapshot SnapshotOf(string dumpPath, string cacheDirectory)
    {
        var snapshot = (Snapshot)RuntimeHelpers.GetUninitializedObject(typeof(Snapshot));
        Set(snapshot, nameof(Snapshot.DumpPath), dumpPath);
        Set(snapshot, nameof(Snapshot.CacheDirectory), cacheDirectory);
        return snapshot;
    }

    private static void Set(Snapshot snapshot, string property, string value) =>
        typeof(Snapshot).GetField($"<{property}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(snapshot, value);
}
