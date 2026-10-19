using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.Analysis;
using Sherlock.Core.Diagnostics;
using Sherlock.Core.HeapModel;
using Sherlock.Core.Profiling;
using Sherlock.Core.Storage;
using Sherlock.Core.Store;

namespace Sherlock.Core;

/// <summary>An opened managed heap snapshot. Queries may run on several threads at once: each cached analysis is
/// computed once, and concurrent callers wait for it.</summary>
public sealed class Snapshot : IDisposable
{
    private readonly DataTarget _dataTarget;
    private readonly SnapshotEntry? _entry;
    private readonly HeapGraphProvider _heapGraph;
    private readonly Lock _provenanceLock = new();
    private SlabFile? _slab;
    private ProvenanceReader? _provenance;
    private Exception? _provenanceFailure;
    private AllocationProfile? _allocations;
    private readonly Once<DumpInfo> _info = new();
    private readonly Once<IReadOnlyList<ModuleInfo>> _modules = new();
    private readonly Once<IReadOnlyList<SegmentInfo>> _segments = new();
    private readonly Once<HeapGenerations> _generations = new();
    private readonly Once<IReadOnlyList<ThreadInfo>> _threads = new();
    private readonly Once<IReadOnlyList<ExceptionInfo>> _exceptions = new();
    private readonly Once<IReadOnlyList<HeapTypeStat>> _histogram = new();
    private readonly Once<DumpHeap> _dumpHeap = new();
    private readonly Once<InboundIndex> _inbound = new();
    private readonly Once<DominatorTree> _dominators = new();
    private readonly Once<FinalizerReport> _finalizers = new();
    private readonly Once<IReadOnlyList<Finding>> _diagnosis = new();
    private bool _disposed;

    private Snapshot(string path, DataTarget dataTarget, ClrInfo clrInfo, ClrRuntime runtime, SnapshotEntry? entry, string cacheDirectory)
    {
        DumpPath = path;
        CacheDirectory = cacheDirectory;
        _dataTarget = dataTarget;
        ClrInfo = clrInfo;
        Runtime = runtime;
        _entry = entry;
        _heapGraph = new HeapGraphProvider(this);
    }

    public string DumpPath { get; }

    /// <summary>Where this dump's analysis caches are kept: its snapshot folder when the store owns it, otherwise a
    /// folder under the store, never next to someone else's file.</summary>
    internal string CacheDirectory { get; }
    public ClrRuntime Runtime { get; }
    public bool HasProvenance => _entry?.ProvenancePath is not null;
    public bool HasCorrelation => _entry?.HasCorrelation ?? false;

    internal DataTarget DataTarget => _dataTarget;
    internal ClrInfo ClrInfo { get; }

    public static Snapshot Open(string path) => Open(path, null, SnapshotStore.CacheDirectory(SnapshotStore.DefaultRoot, path));

    internal static Snapshot Open(string path, SnapshotEntry? entry, string cacheDirectory)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Dump file not found.", path);
        }

        DataTarget? dataTarget = null;
        try
        {
            path = Path.GetFullPath(path);
            dataTarget = DataTarget.LoadDump(path);
            if (dataTarget.ClrVersions.Length == 0)
            {
                throw new DumpAnalysisException("No .NET runtime was found in this dump. Sherlock analyzes managed CLR dumps.");
            }
            ClrInfo clrInfo = dataTarget.ClrVersions[0];
            return new Snapshot(path, dataTarget, clrInfo, clrInfo.CreateRuntime(), entry, cacheDirectory);
        }
        catch
        {
            dataTarget?.Dispose();
            throw;
        }
    }

    public DumpInfo Info => _info.Get(() => new DumpInspector(this).Inspect());
    public IReadOnlyList<ModuleInfo> Modules => _modules.Get(() => new RuntimeAnalyzer(this).GetModules());
    public IReadOnlyList<SegmentInfo> Segments => _segments.Get(() => new RuntimeAnalyzer(this).GetSegments());
    public HeapGenerations Generations => _generations.Get(() => new RuntimeAnalyzer(this).GetGenerations());
    public IReadOnlyList<ThreadInfo> Threads => _threads.Get(() => new ThreadAnalyzer(this).GetThreads());
    public IReadOnlyList<ExceptionInfo> Exceptions => GetExceptions();
    public IReadOnlyList<HeapTypeStat> Histogram => GetHistogram();
    public DominatorTree Dominators => GetDominatorTree();

    public IReadOnlyList<ExceptionInfo> GetExceptions(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _exceptions.Get(() => new ExceptionAnalyzer(this).FindExceptions(cancellationToken), cancellationToken);
    }

    /// <summary>Objects by type, largest first. Comes from the heap graph when one is cached; otherwise walks the heap.</summary>
    public IReadOnlyList<HeapTypeStat> GetHistogram(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _histogram.Get(() => BuildHistogram(cancellationToken), cancellationToken);
    }

    /// <summary>The heap's segments and types, with its bytes mapped from the dump, for parallel walks.</summary>
    internal DumpHeap GetDumpHeap(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dumpHeap.Get(() => DumpHeap.Open(DumpPath, Runtime), cancellationToken);
    }

    public DominatorTree GetDominatorTree(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _dominators.Get(() => new DominatorAnalyzer(this).Build(cancellationToken), cancellationToken);
    }

    /// <summary>The snapshot's allocation profile, built once; views on several threads share it. Throws
    /// <see cref="InvalidDataException"/> when the profile can't be read.</summary>
    public AllocationProfile? Allocations
    {
        get
        {
            lock (_provenanceLock)
            {
                ProvenanceReader? provenance = GetProvenance();
                return provenance is null ? null : _allocations ??= AllocationProfileReader.From(provenance);
            }
        }
    }

    public ObjectDetail Inspect(ulong address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new ObjectInspector(this).Inspect(address);
    }

    public ObjectValue InspectValue(ulong address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new ObjectInspector(this).InspectValue(address);
    }

    public InspectionPage InspectChildren(ObjectValue value, int startIndex = 0, int count = 64, bool raw = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new ObjectInspector(this).InspectChildren(value, startIndex, count, raw);
    }
    public IReadOnlyList<GcRootPath> Roots(ulong address, CancellationToken cancellationToken = default) =>
        RootAnalyzer.Find(GetHeapGraph(cancellationToken), GetInbound(cancellationToken), CacheDirectory, address, cancellationToken);
    public InstanceListing Instances(string filter, int limit = 20, CancellationToken cancellationToken = default, bool exact = false) =>
        new HeapAnalyzer(this).ListInstances(filter, limit, cancellationToken, exact);

    public IReadOnlyList<DuplicateString> DuplicateStrings(int limit = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new HeapAnalyzer(this).FindDuplicateStrings(limit, cancellationToken);
    }

    public FinalizerReport Finalizers(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _finalizers.Get(() => new FinalizerAnalyzer(this).Analyze(cancellationToken), cancellationToken);
    }

    public IReadOnlyList<EventSubscription> EventHandlerLeaks(int minSubscribers = 16, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new EventHandlerAnalyzer(this).Analyze(minSubscribers, cancellation: cancellationToken);
    }

    public IReadOnlyList<Finding> Diagnose(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _diagnosis.Get(() => new HeapDoctor(this).Diagnose(cancellationToken), cancellationToken);
    }

    /// <summary>See <see cref="Profiling.ProvenanceReader.FramesFor"/>. Throws <see cref="InvalidDataException"/> when the
    /// profile can't be read.</summary>
    public IReadOnlyList<MethodFrame>? WhoAllocated(ulong address)
    {
        if (!HasCorrelation)
        {
            return null;
        }
        lock (_provenanceLock)
        {
            return GetProvenance()?.FramesFor(address);
        }
    }

    internal HeapGraph GetHeapGraph(CancellationToken cancellationToken = default) => _heapGraph.Get(cancellationToken);

    /// <summary>Who references each object, from the cache or built into it from the heap graph.</summary>
    internal InboundIndex GetInbound(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _inbound.Get(() =>
        {
            Directory.CreateDirectory(CacheDirectory);
            return InboundIndex.Open(GetHeapGraph(cancellationToken), Path.Combine(CacheDirectory, "inbound.slab"), cancellationToken);
        }, cancellationToken);
    }
    internal HeapGraph? TryGetCachedHeapGraph(CancellationToken cancellationToken = default) => _heapGraph.TryGetCachedOrOnDisk(cancellationToken);

    // Callers hold _provenanceLock. A profile that can't be read fails the same way every time, without reopening it.
    private ProvenanceReader? GetProvenance()
    {
        if (_provenance is not null)
        {
            return _provenance;
        }
        if (_provenanceFailure is not null)
        {
            throw new InvalidDataException(_provenanceFailure.Message, _provenanceFailure);
        }
        string? path = _entry?.ProvenancePath;
        if (path is null)
        {
            return null;
        }
        SlabFile? slab = null;
        try
        {
            slab = ProfileFormat.Open(path);
            _provenance = new ProvenanceReader(slab);
            _slab = slab;
            return _provenance;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            slab?.Dispose();
            _provenanceFailure = ex;
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    private IReadOnlyList<HeapTypeStat> BuildHistogram(CancellationToken cancellationToken)
    {
        if (TryGetCachedHeapGraph(cancellationToken) is not { } graph)
        {
            return HeapWalk.Histogram(GetDumpHeap(cancellationToken), cancellationToken);
        }

        var rows = graph.Histogram();
        var stats = new List<HeapTypeStat>(rows.Length);
        foreach ((string typeName, long count, ulong totalSize) in rows)
        {
            if (count > 0)
            {
                stats.Add(new HeapTypeStat(typeName, count, totalSize));
            }
        }
        stats.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));
        return stats;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _slab?.Dispose();
        _dominators.Value?.Dispose();
        _inbound.Value?.Dispose();
        _heapGraph.Dispose();
        _dumpHeap.Value?.Dispose();
        Runtime.Dispose();
        _dataTarget.Dispose();
    }
}
