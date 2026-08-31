using System;
using System.IO;
using System.Threading;

namespace Sherlock.Core.HeapModel;

/// <summary>
/// Supplies the <see cref="HeapGraph"/> for a dump, hiding the extract → persist → reload lifecycle
/// behind one call. First request extracts the graph (raw, DAC-bypassing) and writes it to a
/// <c>.slab</c> beside the dump; later requests (including a fresh process opening the same snapshot)
/// load that file and skip extraction.
/// </summary>
public sealed class HeapGraphProvider(Snapshot snapshot) : IDisposable
{
    private HeapGraph? _cached;

    /// <summary>The dump's heap graph. Cached for the session and backed on disk by a sidecar so
    /// reopening is instant. Set <paramref name="persist"/> false to skip writing the sidecar.</summary>
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

    /// <summary>The sidecar path for a dump: <c>&lt;dump&gt;.heapgraph.slab</c>, next to the dump.</summary>
    public static string SidecarPath(string dumpPath) => Path.GetFileName(dumpPath) == "heap.dmp" ? Path.Combine(Path.GetDirectoryName(dumpPath)!, "heapgraph.slab") : dumpPath + ".heapgraph.slab";

    /// <summary>Returns the graph only if already available (cached this session or loadable from the
    /// sidecar), without triggering a fresh extraction. Lets a cheap analysis (e.g. histogram) ride an
    /// already-built graph without paying to build one.</summary>
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
            return null; // corrupt / partial sidecar; rebuild
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
            // Best-effort persistence: a read-only dump directory just means we rebuild next time.
        }
    }

    /// <summary>Releases the cached graph and, when it's mmap-backed, the underlying file mapping.</summary>
    public void Dispose() => _cached?.Dispose();
}
