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
            HeapGraphStore.Save(HeapGraphProvider.SidecarPath(dump), graph, dump);
        }
        using var provider = new HeapGraphProvider(SnapshotOf(dump));

        HeapGraph?[] graphs = OnceTests.RunTogether(16, () => provider.TryGetCachedOrOnDisk());

        Assert.NotNull(graphs[0]);
        Assert.All(graphs, graph => Assert.Same(graphs[0], graph));
    }

    // Only the dump path: loading a saved graph touches nothing else.
    private static Snapshot SnapshotOf(string dumpPath)
    {
        var snapshot = (Snapshot)RuntimeHelpers.GetUninitializedObject(typeof(Snapshot));
        typeof(Snapshot).GetField($"<{nameof(Snapshot.DumpPath)}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(snapshot, dumpPath);
        return snapshot;
    }
}
