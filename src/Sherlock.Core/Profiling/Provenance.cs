using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sherlock.Core.Storage;

namespace Sherlock.Core.Profiling;

/// <summary>An allocation site: a stack plus its alloc/survived byte+object counters. Mirrors native <c>AllocationRecord</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AllocationRecord
{
    public uint StackId;
    public uint TypeId;   // v2+: frameId of the allocated type name in the shared Frames table; 0 in v1 slabs
    public ulong AllocBytes;
    public ulong AllocCount;
    public ulong SurvivedBytes;
    public ulong SurvivedCount;
}

/// <summary>A live object's provenance: address -> allocating stack id. Stored sorted by address. Mirrors native <c>CorrelationRecord</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CorrelationRecord
{
    public ulong Address;
    public uint StackId;
    public uint Reserved;
}

/// <summary>Version constant for the profile/correlation record sections (mirrors native <c>kProfileVersion</c>).</summary>
public static class ProfileFormat
{
    public const ushort Version = 1;
}

/// <summary>
/// Builds a provenance container: one shared interned stack table backing both the allocation
/// profile and per-object correlation. Managed mirror of the native writer; used for tests and tooling.
/// </summary>
public sealed class ProvenanceWriter
{
    private readonly StackTableBuilder _stacks = new();
    private readonly List<AllocationRecord> _allocs = [];
    private readonly List<CorrelationRecord> _corr = [];

    public uint InternFrame(string name) => _stacks.InternFrame(name);

    /// <summary>Interns a stack (its frames, then the sequence) and returns its shared id.</summary>
    public uint InternStack(ReadOnlySpan<string> frames)
    {
        Span<uint> ids = frames.Length <= 64 ? stackalloc uint[frames.Length] : new uint[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            ids[i] = _stacks.InternFrame(frames[i]);
        }
        return _stacks.InternStack(ids);
    }

    public void AddAllocation(uint stackId, ulong allocBytes, ulong allocCount, ulong survivedBytes, ulong survivedCount)
        => _allocs.Add(new AllocationRecord
        {
            StackId = stackId,
            AllocBytes = allocBytes,
            AllocCount = allocCount,
            SurvivedBytes = survivedBytes,
            SurvivedCount = survivedCount,
        });

    /// <summary>Records that the live object at <paramref name="address"/> was allocated by <paramref name="stackId"/>.</summary>
    public void AddObject(ulong address, uint stackId)
        => _corr.Add(new CorrelationRecord { Address = address, StackId = stackId });

    public void WriteTo(ContainerWriter w)
    {
        _stacks.WriteTo(w);
        if (_allocs.Count > 0)
        {
            w.AddRecords(SectionType.Allocations, ProfileFormat.Version, CollectionsMarshal.AsSpan(_allocs));
        }
        if (_corr.Count > 0)
        {
            // Sort by address so the reader can binary-search. Chunked to match the native writer and
            // stay under the reader's per-section cap: one 16-byte record per live object overflows a
            // single section past ~134M objects.
            _corr.Sort(static (a, b) => a.Address.CompareTo(b.Address));
            w.AddChunkedRecords(SectionType.Correlation, ProfileFormat.Version, CollectionsMarshal.AsSpan(_corr));
        }
    }
}

/// <summary>Read-only view over a provenance container: allocation + correlation columns, plus the
/// stack table to resolve their ids. The correlation column may span many chunk sections and exceed
/// 2&nbsp;GB, and is <c>long</c>-indexed, so <c>whoalloc</c> works past ~134M live objects.</summary>
public sealed class ProvenanceReader
{
    private readonly Column<CorrelationRecord> _corr;

    public StackTable Stacks { get; }

    /// <summary>The allocation sites (one per distinct call-stack + type), long-indexed.</summary>
    public Column<AllocationRecord> Allocations { get; }

    /// <summary>Version of the Allocations section; >= 2 means each record carries a real <c>TypeId</c>.</summary>
    public ushort AllocationsVersion { get; }

    public ProvenanceReader(SlabFile slab)
    {
        Stacks = StackTable.Read(slab);
        Allocations = slab.GetColumn<AllocationRecord>(SectionType.Allocations);
        AllocationsVersion = slab.SectionVersion(SectionType.Allocations);
        _corr = slab.GetColumn<CorrelationRecord>(SectionType.Correlation);
    }

    /// <summary>The number of tracked live objects (correlation records).</summary>
    public long CorrelationCount => _corr.Length;

    /// <summary>Binary-searches the address-sorted correlation column for an object's allocating stack
    /// id. Long-indexed and chunk-transparent: the column may be several on-disk sections.</summary>
    public bool TryGetStack(ulong address, out uint stackId)
    {
        long lo = 0, hi = _corr.Length - 1;
        while (lo <= hi)
        {
            long mid = lo + ((hi - lo) >> 1);
            ulong a = _corr[mid].Address;
            if (a == address)
            {
                stackId = _corr[mid].StackId;
                return true;
            }
            if (a < address)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        stackId = 0;
        return false;
    }

    /// <summary>Resolves an object address directly to its allocation stack string, or null if untracked.</summary>
    public string? StackFor(ulong address) => TryGetStack(address, out uint stackId) ? Stacks.FormatStack(stackId) : null;
}
