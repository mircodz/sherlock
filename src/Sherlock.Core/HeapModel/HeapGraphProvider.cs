using System;
using System.IO;
using System.Threading;

namespace Sherlock.Core.HeapModel;

/// <summary>Loads a compatible heap graph, or builds one from the dump into the cache. Concurrent callers share one graph:
/// the first loads or builds it while the others wait.</summary>
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
            string path = CachePath;
            HeapGraph result = TryLoad(path) ?? Build(path, cancellationToken);
            Volatile.Write(ref _graph, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string CachePath => Path.Combine(snapshot.CacheDirectory, "heapgraph.slab");

    /// <summary>Returns an existing graph without building one, waiting for a load or build in progress.</summary>
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
            if (_graph is null && TryLoad(CachePath) is { } loaded)
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

    // The graph is written to the cache as it's built, then mapped like any cached graph.
    private HeapGraph Build(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        HeapGraphBuilder.Build(snapshot.GetDumpHeap(cancellationToken), snapshot.DumpPath, path, cancellationToken);
        return HeapGraphStore.Load(path, snapshot.DumpPath) ?? throw new InvalidDataException($"The heap graph written to '{path}' doesn't match its dump.");
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

    /// <summary>Releases the graph, after any load or build in progress finishes.</summary>
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
