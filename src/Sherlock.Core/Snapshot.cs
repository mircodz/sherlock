using System;
using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.Analysis;
using Sherlock.Core.Diagnostics;
using Sherlock.Core.Profiling;
using Sherlock.Core.Storage;
using Sherlock.Core.Store;

namespace Sherlock.Core;

/// <summary>
/// A loaded snapshot: the dump (for heap analysis) plus, when present, its allocation provenance.
/// Every analysis is lazy and cached for this snapshot's lifetime; loading another snapshot builds a
/// fresh one, so the caches never go stale.
/// </summary>
public sealed class Snapshot(DumpSession dump, SnapshotEntry? entry = null) : IDisposable
{
    public ClrRuntime Runtime => dump.Runtime;
    public string DumpPath => dump.DumpPath;

    // Dump analyses, lazy + cached.
    private DumpInfo? _info;
    public DumpInfo Info => _info ??= new DumpInspector(dump).Inspect();

    private IReadOnlyList<ModuleInfo>? _modules;
    public IReadOnlyList<ModuleInfo> Modules => _modules ??= new RuntimeAnalyzer(dump).GetModules();

    private IReadOnlyList<SegmentInfo>? _segments;
    public IReadOnlyList<SegmentInfo> Segments => _segments ??= new RuntimeAnalyzer(dump).GetSegments();

    private IReadOnlyList<ThreadInfo>? _threads;
    public IReadOnlyList<ThreadInfo> Threads => _threads ??= new ThreadAnalyzer(dump).GetThreads();

    private IReadOnlyList<ExceptionInfo>? _exceptions;
    public IReadOnlyList<ExceptionInfo> Exceptions => _exceptions ??= new ExceptionAnalyzer(dump).FindExceptions();

    public IReadOnlyList<HeapTypeStat> Histogram => dump.GetHistogram();
    public DominatorTree Dominators => dump.GetDominatorTree();

    // Parameterized queries.
    public ObjectDetail Inspect(ulong address) => new ObjectInspector(dump).Inspect(address);
    public IReadOnlyList<GcRootPath> Roots(ulong address, int maxPaths = 1) => new RootAnalyzer(dump).FindRoots(address, maxPaths);
    public InstanceListing Instances(string filter, int limit = 20) => new HeapAnalyzer(dump).ListInstances(filter, limit);
    public IReadOnlyList<DuplicateString> DuplicateStrings(int limit = 20) => new HeapAnalyzer(dump).FindDuplicateStrings(limit);

    private FinalizerReport? _finalizers;
    public FinalizerReport Finalizers() => _finalizers ??= new FinalizerAnalyzer(dump).Analyze();
    public IReadOnlyList<EventSubscription> EventHandlerLeaks(int minSubscribers = 16) => new EventHandlerAnalyzer(dump).Analyze(minSubscribers);

    private IReadOnlyList<Finding>? _diagnosis;
    /// <summary>Sweeps every inspector and reports the obvious problems, ordered by severity.</summary>
    public IReadOnlyList<Finding> Diagnose() => _diagnosis ??= new HeapDoctor(dump).Diagnose();

    // Allocation provenance from the bundled .slab, lazy + cached; the reader stays open for lookups.
    public bool HasProvenance => entry?.ProvenancePath is not null;
    public bool HasCorrelation => entry?.HasCorrelation ?? false;

    private ContainerReader? _container;
    private ProvenanceReader? _provenance;
    private ProvenanceReader? Provenance
    {
        get
        {
            if (_provenance is null && entry?.ProvenancePath is { } path)
            {
                _container = ContainerReader.Open(path);
                _provenance = new ProvenanceReader(_container);
            }
            return _provenance;
        }
    }

    private AllocationProfile? _allocations;
    public AllocationProfile? Allocations => Provenance is { } p ? _allocations ??= AllocationProfileReader.From(p) : null;

    /// <summary>The allocation stack for an object address, or null if untracked or unprofiled.</summary>
    public string? WhoAllocated(ulong address) => HasCorrelation ? Provenance?.StackFor(address) : null;

    public void Dispose()
    {
        _container?.Dispose();
        dump.Dispose();
    }
}
