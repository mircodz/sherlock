using System;
using System.IO;
using System.Threading;

namespace Sherlock.Core.HeapModel;

/// <summary>Loads a compatible heap graph or extracts and caches a new one.</summary>
public sealed class HeapGraphProvider(Snapshot snapshot) : IDisposable
{
    private HeapGraph? _cached;

    public HeapGraph Get(CancellationToken cancellationToken = default, bool persist = true)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        string path = SidecarPath(snapshot.DumpPath);
        if (TryLoad(path) is { } loaded)
        {
            return _cached = loaded;
        }

        HeapGraph graph = new HeapGraphExtractor(snapshot).Extract(cancellationToken);
        if (persist)
        {
            TrySave(path, graph);
        }
        return _cached = graph;
    }

    public static string SidecarPath(string dumpPath) => Path.GetFileName(dumpPath) == "heap.dmp" ? Path.Combine(Path.GetDirectoryName(dumpPath)!, "heapgraph.slab") : dumpPath + ".heapgraph.slab";

    /// <summary>Returns an existing graph without extracting one.</summary>
    public HeapGraph? TryGetCachedOrOnDisk()
    {
        if (_cached is not null)
        {
            return _cached;
        }
        return _cached = TryLoad(SidecarPath(snapshot.DumpPath));
    }

    private HeapGraph? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return HeapGraphStore.Load(path, snapshot.DumpPath);
        }
        catch
        {
            return null;
        }
    }

    private void TrySave(string path, HeapGraph graph)
    {
        try
        {
            HeapGraphStore.Save(path, graph, snapshot.DumpPath);
        }
        catch
        {
            // The in-memory graph remains valid when persistence is unavailable.
        }
    }

    public void Dispose() => _cached?.Dispose();
}
