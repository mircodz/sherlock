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

/// <summary>An opened managed heap snapshot.</summary>
public sealed class Snapshot : IDisposable
{
    private readonly DataTarget _dataTarget;
    private readonly SnapshotEntry? _entry;
    private HeapGraphProvider? _heapGraph;
    private DominatorTree? _dominators;
    private SlabFile? _slab;
    private ProvenanceReader? _provenance;
    private DumpInfo? _info;
    private IReadOnlyList<ModuleInfo>? _modules;
    private IReadOnlyList<SegmentInfo>? _segments;
    private IReadOnlyList<ThreadInfo>? _threads;
    private IReadOnlyList<ExceptionInfo>? _exceptions;
    private IReadOnlyList<HeapTypeStat>? _histogram;
    private AllocationProfile? _allocations;
    private FinalizerReport? _finalizers;
    private IReadOnlyList<Finding>? _diagnosis;
    private bool _disposed;

    private Snapshot(string path, DataTarget dataTarget, ClrInfo clrInfo, ClrRuntime runtime, SnapshotEntry? entry)
    {
        DumpPath = path;
        _dataTarget = dataTarget;
        ClrInfo = clrInfo;
        Runtime = runtime;
        _entry = entry;
    }

    public string DumpPath { get; }
    public ClrRuntime Runtime { get; }
    public bool HasProvenance => _entry?.ProvenancePath is not null;
    public bool HasCorrelation => _entry?.HasCorrelation ?? false;

    internal DataTarget DataTarget => _dataTarget;
    internal ClrInfo ClrInfo { get; }

    public static Snapshot Open(string path) => Open(path, null);

    internal static Snapshot Open(string path, SnapshotEntry? entry)
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
            return new Snapshot(path, dataTarget, clrInfo, clrInfo.CreateRuntime(), entry);
        }
        catch
        {
            dataTarget?.Dispose();
            throw;
        }
    }

    public DumpInfo Info => _info ??= new DumpInspector(this).Inspect();
    public IReadOnlyList<ModuleInfo> Modules => _modules ??= new RuntimeAnalyzer(this).GetModules();
    public IReadOnlyList<SegmentInfo> Segments => _segments ??= new RuntimeAnalyzer(this).GetSegments();
    public IReadOnlyList<ThreadInfo> Threads => _threads ??= new ThreadAnalyzer(this).GetThreads();
    public IReadOnlyList<ExceptionInfo> Exceptions => _exceptions ??= new ExceptionAnalyzer(this).FindExceptions();
    public IReadOnlyList<HeapTypeStat> Histogram => _histogram ??= BuildHistogram();
    public DominatorTree Dominators => _dominators ??= new DominatorAnalyzer(this).Build();

    public AllocationProfile? Allocations
    {
        get
        {
            ProvenanceReader? provenance = GetProvenance();
            return provenance is null ? null : _allocations ??= AllocationProfileReader.From(provenance);
        }
    }

    public ObjectDetail Inspect(ulong address) => new ObjectInspector(this).Inspect(address);
    public IReadOnlyList<GcRootPath> Roots(ulong address, CancellationToken cancellationToken = default) => RootAnalyzer.Find(GetHeapGraph(cancellationToken), address, cancellationToken);
    public InstanceListing Instances(string filter, int limit = 20) => new HeapAnalyzer(this).ListInstances(filter, limit);
    public IReadOnlyList<DuplicateString> DuplicateStrings(int limit = 20) => new HeapAnalyzer(this).FindDuplicateStrings(limit);
    public FinalizerReport Finalizers() => _finalizers ??= new FinalizerAnalyzer(this).Analyze();
    public IReadOnlyList<EventSubscription> EventHandlerLeaks(int minSubscribers = 16) => new EventHandlerAnalyzer(this).Analyze(minSubscribers);
    public IReadOnlyList<Finding> Diagnose() => _diagnosis ??= new HeapDoctor(this).Diagnose();
    public string? WhoAllocated(ulong address) => HasCorrelation ? GetProvenance()?.StackFor(address) : null;

    internal HeapGraph GetHeapGraph(CancellationToken cancellationToken = default) => (_heapGraph ??= new HeapGraphProvider(this)).Get(cancellationToken);
    internal DominatorTree GetDominatorTree(CancellationToken cancellationToken = default) => _dominators ??= new DominatorAnalyzer(this).Build(cancellationToken);

    private ProvenanceReader? GetProvenance()
    {
        if (_provenance is not null)
        {
            return _provenance;
        }
        string? path = _entry?.ProvenancePath;
        if (path is null)
        {
            return null;
        }
        _slab = SlabFile.Open(path);
        return _provenance = new ProvenanceReader(_slab);
    }

    private IReadOnlyList<HeapTypeStat> BuildHistogram()
    {
        HeapGraphProvider provider = _heapGraph ??= new HeapGraphProvider(this);
        if (provider.TryGetCachedOrOnDisk() is not { } graph || graph.Histogram() is not { } rows)
        {
            return new HeapAnalyzer(this).GetStatistics();
        }

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
        _heapGraph?.Dispose();
        Runtime.Dispose();
        _dataTarget.Dispose();
    }
}
