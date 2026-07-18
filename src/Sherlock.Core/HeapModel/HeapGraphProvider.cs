using System;
using System.IO;
using System.Threading;

namespace Sherlock.Core.HeapModel;

/// <summary>
/// Supplies the <see cref="HeapGraph"/> for a dump, hiding the whole extract → persist → reload
/// lifecycle behind one call. On first request it extracts the graph (raw, DAC-bypassing) and writes
/// it to a <c>.slab</c> beside the dump; on later requests — including a fresh process opening the same
/// snapshot — it loads that file and skips extraction entirely. Callers just ask for a graph and never
/// see whether it was built or reloaded.
/// </summary>
public sealed class HeapGraphProvider(DumpSession session) : IDisposable
{
    private HeapGraph? _cached;

    /// <summary>The dump's heap graph. Cached for the session; backed on disk by a sidecar so reopening
    /// the snapshot is instant. Set <paramref name="persist"/> false to skip writing the sidecar.</summary>
    public HeapGraph Get(CancellationToken cancellationToken = default, bool persist = true)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        string path = SidecarPath(session.DumpPath);
        if (TryLoad(path) is { } loaded)
        {
            return _cached = loaded;
        }

        HeapGraph graph = new HeapGraphExtractor(session).Extract(cancellationToken);
        if (persist)
        {
            TrySave(path, graph);
        }
        return _cached = graph;
    }

    /// <summary>The sidecar path for a dump: <c>&lt;dump&gt;.heapgraph.slab</c>, next to the dump.</summary>
    public static string SidecarPath(string dumpPath) => dumpPath + ".heapgraph.slab";

    /// <summary>Returns the graph only if it is already available — cached in this session or loadable
    /// from the sidecar — without triggering a (potentially slow) fresh extraction. Lets a cheap
    /// analysis (e.g. histogram) ride an already-built graph but not pay for building one.</summary>
    public HeapGraph? TryGetCachedOrOnDisk()
    {
        if (_cached is not null)
        {
            return _cached;
        }
        return _cached = TryLoad(SidecarPath(session.DumpPath));
    }

    private static HeapGraph? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return HeapGraphStore.Load(path);
        }
        catch
        {
            return null; // corrupt / partial sidecar — fall back to rebuilding
        }
    }

    private static void TrySave(string path, HeapGraph graph)
    {
        try
        {
            HeapGraphStore.Save(path, graph);
        }
        catch
        {
            // Best-effort persistence: a read-only dump directory just means we rebuild next time.
        }
    }

    /// <summary>Releases the cached graph and, when it's mmap-backed, the underlying file mapping.</summary>
    public void Dispose() => _cached?.Dispose();
}
