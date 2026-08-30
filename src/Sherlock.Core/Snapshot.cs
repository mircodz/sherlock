using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.Analysis;
using Sherlock.Core.Diagnostics;
using Sherlock.Core.Profiling;
using Sherlock.Core.Storage;
using Sherlock.Core.Store;

namespace Sherlock.Core;

/// <summary>
/// A loaded snapshot: the dump plus, when present, its allocation provenance. Every analysis is
/// lazy and cached for this snapshot's lifetime.
/// </summary>
public sealed class Snapshot(DumpSession dump, SnapshotEntry? entry = null) : IDisposable
{
    public ClrRuntime Runtime => dump.Runtime;

    [field: AllowNull, MaybeNull]
    public DumpInfo Info => field ??= new DumpInspector(dump).Inspect();

    [field: AllowNull, MaybeNull]
    public IReadOnlyList<ModuleInfo> Modules => field ??= new RuntimeAnalyzer(dump).GetModules();

    [field: AllowNull, MaybeNull]
    public IReadOnlyList<SegmentInfo> Segments => field ??= new RuntimeAnalyzer(dump).GetSegments();

    [field: AllowNull, MaybeNull]
    public IReadOnlyList<ThreadInfo> Threads => field ??= new ThreadAnalyzer(dump).GetThreads();

    [field: AllowNull, MaybeNull]
    public IReadOnlyList<ExceptionInfo> Exceptions => field ??= new ExceptionAnalyzer(dump).FindExceptions();

    public IReadOnlyList<HeapTypeStat> Histogram => dump.GetHistogram();

    // Dominators over the persisted DAC-bypassing heap graph, cached on disk so reopening skips recompute.
    public DominatorTree Dominators => dump.GetDominatorTree();

    // Parameterized queries.
    public ObjectDetail Inspect(ulong address) => new ObjectInspector(dump).Inspect(address);
    // gcroot over the persisted dominator tree (DAC-free, O(path length)) instead of a ClrMD BFS from
    // every root. Same answer, no multi-minute heap walk.
    public IReadOnlyList<GcRootPath> Roots(ulong address, int maxPaths = 1, CancellationToken cancellationToken = default) =>
        new RootAnalyzerV2(dump).FindRoots(address, maxPaths, cancellationToken);

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

    private SlabFile? _slab;

    private ProvenanceReader? Provenance
    {
        get
        {
            if (field is null && entry?.ProvenancePath is { } path)
            {
                _slab = SlabFile.Open(path);
                field = new ProvenanceReader(_slab);
            }
            return field;
        }
    }

    public AllocationProfile? Allocations => Provenance is { } p ? field ??= AllocationProfileReader.From(p) : null;

    /// <summary>The allocation stack for an object address, or null if untracked or unprofiled.</summary>
    public string? WhoAllocated(ulong address) => HasCorrelation ? Provenance?.StackFor(address) : null;

    public void Dispose()
    {
        _slab?.Dispose();
        dump.Dispose();
    }
}
