using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Sherlock.Core.Storage;

/// <summary>An allocation site: a stack plus its alloc/survived byte+object counters. Mirrors native <c>AllocationRecord</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AllocationRecord
{
    public uint StackId;
    public uint Reserved;
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
/// profile and per-object correlation. Managed mirror of the native writer (which does this in
/// production); used here for tests and tooling.
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
            w.AddRecords<AllocationRecord>(SectionType.Allocations, ProfileFormat.Version, CollectionsMarshal.AsSpan(_allocs));
        }
        if (_corr.Count > 0)
        {
            // Sort by address so the reader can binary-search; emitted only when there's provenance.
            _corr.Sort(static (a, b) => a.Address.CompareTo(b.Address));
            w.AddRecords<CorrelationRecord>(SectionType.Correlation, ProfileFormat.Version, CollectionsMarshal.AsSpan(_corr));
        }
    }
}

/// <summary>Read-only view over a provenance container: allocation + correlation records, plus the stack table to resolve their ids.</summary>
public sealed class ProvenanceReader
{
    private readonly ReadOnlyMemory<byte> _allocs;
    private readonly ReadOnlyMemory<byte> _corr;

    public StackTable Stacks { get; }

    public ProvenanceReader(ContainerReader container)
    {
        Stacks = StackTable.Read(container);
        _allocs = container.TryGetSection(SectionType.Allocations, out Section a) ? a.Data : default;
        _corr = container.TryGetSection(SectionType.Correlation, out Section c) ? c.Data : default;
    }

    public ReadOnlySpan<AllocationRecord> Allocations => MemoryMarshal.Cast<byte, AllocationRecord>(_allocs.Span);
    public ReadOnlySpan<CorrelationRecord> Correlation => MemoryMarshal.Cast<byte, CorrelationRecord>(_corr.Span);

    /// <summary>Binary-searches the sorted correlation records for an object's allocating stack id.</summary>
    public bool TryGetStack(ulong address, out uint stackId)
    {
        ReadOnlySpan<CorrelationRecord> recs = Correlation;
        int lo = 0, hi = recs.Length - 1;
        while (lo <= hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            ulong a = recs[mid].Address;
            if (a == address)
            {
                stackId = recs[mid].StackId;
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
