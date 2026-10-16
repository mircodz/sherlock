using System;
using System.IO;
using System.Threading;

namespace Sherlock.Core.HeapModel;

/// <summary>Loads a compatible heap graph or extracts and caches a new one. Concurrent callers share one graph: the
/// first loads or extracts it while the others wait.</summary>
public sealed class HeapGraphProvider(Snapshot snapshot) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HeapGraph? _graph;
    private bool _disposed;

    public HeapGraph Get(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _graph) is { } graph)
        {
            return graph;
        }
        _gate.Wait(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_graph is { } cached)
            {
                return cached;
            }
            string path = SidecarPath(snapshot.DumpPath);
            HeapGraph result = TryLoad(path) ?? Extract(path, cancellationToken);
            Volatile.Write(ref _graph, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string SidecarPath(string dumpPath) => Path.GetFileName(dumpPath) == "heap.dmp" ? Path.Combine(Path.GetDirectoryName(dumpPath)!, "heapgraph.slab") : dumpPath + ".heapgraph.slab";

    /// <summary>Returns an existing graph without extracting one, waiting for a load or extraction in progress.</summary>
    public HeapGraph? TryGetCachedOrOnDisk(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _graph) is { } graph)
        {
            return graph;
        }
        _gate.Wait(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_graph is null && TryLoad(SidecarPath(snapshot.DumpPath)) is { } loaded)
            {
                Volatile.Write(ref _graph, loaded);
            }
            return _graph;
        }
        finally
        {
            _gate.Release();
        }
    }

    private HeapGraph Extract(string path, CancellationToken cancellationToken)
    {
        HeapGraph graph = new HeapGraphExtractor(snapshot).Extract(cancellationToken);
        TrySave(path, graph);
        return graph;
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

    /// <summary>Releases the graph, after any load or extraction in progress finishes.</summary>
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _disposed = true;
            _graph?.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }
}
