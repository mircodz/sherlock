using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Sherlock.Core.Diagnostics;
using Sherlock.Core.Profiling;
using Sherlock.Core.Store;
using Sherlock.Heap;
using Sherlock.Storage;

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
    public bool HasCorrelation => _entry?.HasCorrelation ?? false;

    public static Snapshot Open(string path) => Open(path, null, SnapshotStore.CacheDirectory(SnapshotStore.DefaultRoot, path));

    internal static Snapshot Open(string path, SnapshotEntry? entry, string cacheDirectory) =>
        new(HeapDump.Open(path, cacheDirectory), entry);

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
