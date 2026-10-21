using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Sherlock.Core.Diagnostics;
using Sherlock.Core.Profiling;
using Sherlock.Core.Store;
using Sherlock.Heap;
using Sherlock.Heap.Analysis;
using Sherlock.Storage;
using ClrRuntime = Microsoft.Diagnostics.Runtime.ClrRuntime;

namespace Sherlock.Core;

/// <summary>An opened snapshot: its heap, and the allocation profile recorded with it when there is one. Queries may
/// run on several threads at once: each cached analysis is computed once, and concurrent callers wait for it.</summary>
public sealed class Snapshot : IDisposable
{
    private readonly SnapshotEntry? _entry;
    private readonly Lock _provenanceLock = new();
    private SlabFile? _slab;
    private ProvenanceReader? _provenance;
    private Exception? _provenanceFailure;
    private AllocationProfile? _allocations;
    private readonly Once<IReadOnlyList<Finding>> _diagnosis = new();
    private bool _disposed;

    private Snapshot(HeapDump heap, SnapshotEntry? entry)
    {
        Heap = heap;
        _entry = entry;
    }

    /// <summary>The dump's heap. Its caches go in the snapshot's folder when the store owns the dump, otherwise in a
    /// folder under the store, never next to someone else's file.</summary>
    public HeapDump Heap { get; }
    public string DumpPath => Heap.DumpPath;
    public ClrRuntime Runtime => Heap.Runtime;
    public bool HasProvenance => _entry?.ProvenancePath is not null;
    public bool HasCorrelation => _entry?.HasCorrelation ?? false;

    public static Snapshot Open(string path) => Open(path, null, SnapshotStore.CacheDirectory(SnapshotStore.DefaultRoot, path));

    internal static Snapshot Open(string path, SnapshotEntry? entry, string cacheDirectory) =>
        new(HeapDump.Open(path, cacheDirectory), entry);

    public DumpInfo Info => Heap.Info;
    public IReadOnlyList<ModuleInfo> Modules => Heap.Modules;
    public IReadOnlyList<SegmentInfo> Segments => Heap.Segments;
    public HeapGenerations Generations => Heap.Generations;
    public IReadOnlyList<ThreadInfo> Threads => Heap.Threads;
    public IReadOnlyList<ExceptionInfo> Exceptions => Heap.Exceptions;
    public IReadOnlyList<HeapTypeStat> Histogram => Heap.Histogram;
    public DominatorTree Dominators => Heap.Dominators;

    public IReadOnlyList<ExceptionInfo> GetExceptions(CancellationToken cancellationToken = default) => Heap.GetExceptions(cancellationToken);
    public IReadOnlyList<HeapTypeStat> GetHistogram(CancellationToken cancellationToken = default) => Heap.GetHistogram(cancellationToken);
    public DominatorTree GetDominatorTree(CancellationToken cancellationToken = default) => Heap.GetDominatorTree(cancellationToken);

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

    public ObjectDetail Inspect(ulong address) => Heap.Inspect(address);
    public ObjectValue InspectValue(ulong address) => Heap.InspectValue(address);
    public InspectionPage InspectChildren(ObjectValue value, int startIndex = 0, int count = 64, bool raw = false) =>
        Heap.InspectChildren(value, startIndex, count, raw);
    public IReadOnlyList<GcRootPath> Roots(ulong address, CancellationToken cancellationToken = default) => Heap.Roots(address, cancellationToken);
    public InstanceListing Instances(string filter, int limit = 20, CancellationToken cancellationToken = default, bool exact = false) =>
        Heap.Instances(filter, limit, cancellationToken, exact);
    public IReadOnlyList<DuplicateString> DuplicateStrings(int limit = 20, CancellationToken cancellationToken = default) =>
        Heap.DuplicateStrings(limit, cancellationToken);
    public FinalizerReport Finalizers(CancellationToken cancellationToken = default) => Heap.Finalizers(cancellationToken);
    public IReadOnlyList<EventSubscription> EventHandlerLeaks(int minSubscribers = 16, CancellationToken cancellationToken = default) =>
        Heap.EventHandlerLeaks(minSubscribers, cancellationToken);

    public IReadOnlyList<Finding> Diagnose(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _diagnosis.Get(() => new HeapDoctor(Heap).Diagnose(cancellationToken), cancellationToken);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _slab?.Dispose();
        Heap.Dispose();
    }
}
