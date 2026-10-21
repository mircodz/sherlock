using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Sherlock.Storage;

namespace Sherlock.Core.Profiling;

/// <summary>An allocation site: a stack plus its alloc/survived byte+object counters. Mirrors native <c>AllocationRecord</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AllocationRecord
{
    public uint StackId;
    public uint TypeId;   // index into the Types section
    public ulong AllocBytes;
    public ulong AllocCount;
    public ulong SurvivedBytes;
    public ulong SurvivedCount;
}

/// <summary>The profile kind's version, covering every section (mirrors native <c>kProfileVersion</c>).</summary>
public static class ProfileFormat
{
    public const uint Version = 2;

    public static SlabFile Open(string path) => SlabFile.Open(path, SlabKind.Profile, Version);
}

/// <summary>
/// Builds allocation and correlation sections sharing one symbol table, as the native profiler writes them;
/// Sherlock writes profiles only in tests.
/// </summary>
public sealed class ProvenanceWriter
{
    private readonly SymbolWriter _symbols = new();
    private readonly List<AllocationRecord> _allocs = [];
    private readonly List<ulong> _objectAddresses = [];
    private readonly List<uint> _objectStacks = [];

    public uint InternType(string name) => _symbols.InternType(name);

    /// <summary>Interns a stack's methods, root to leaf, then the stack.</summary>
    public uint InternStack(IEnumerable<MethodFrame> methods)
    {
        var ids = new List<uint>();
        foreach (MethodFrame method in methods)
        {
            ids.Add(_symbols.InternMethod(method));
        }
        return _symbols.InternStack(CollectionsMarshal.AsSpan(ids));
    }

    public void AddAllocation(uint stackId, uint typeId, ulong allocBytes, ulong allocCount, ulong survivedBytes, ulong survivedCount)
        => _allocs.Add(new AllocationRecord
        {
            StackId = stackId,
            TypeId = typeId,
            AllocBytes = allocBytes,
            AllocCount = allocCount,
            SurvivedBytes = survivedBytes,
            SurvivedCount = survivedCount,
        });

    /// <summary>Records that the live object at <paramref name="address"/> was allocated by <paramref name="stackId"/>.</summary>
    public void AddObject(ulong address, uint stackId)
    {
        _objectAddresses.Add(address);
        _objectStacks.Add(stackId);
    }

    public void WriteTo(ContainerWriter w)
    {
        _symbols.WriteTo(w);
        if (_allocs.Count > 0)
        {
            w.AddRecords(SectionType.Allocations, CollectionsMarshal.AsSpan(_allocs));
        }
        if (_objectAddresses.Count > 0)
        {
            // Readers binary-search by address.
            ulong[] addresses = [.. _objectAddresses];
            uint[] stacks = [.. _objectStacks];
            Array.Sort(addresses, stacks);
            w.AddRecords<ulong>(SectionType.ObjectAddresses, addresses);
            w.AddRecords<uint>(SectionType.ObjectStacks, stacks);
        }
    }
}

/// <summary>Allocation records and live objects' allocating stacks, with their shared symbols.
/// The supplied slab must remain open while reading its columns.</summary>
public sealed class ProvenanceReader
{
    private readonly Column<ulong> _objectAddresses;
    private readonly Column<uint> _objectStacks;

    public SymbolTable Symbols { get; }

    /// <summary>The allocation sites (one per distinct call-stack + type), long-indexed.</summary>
    public Column<AllocationRecord> Allocations { get; }

    public ProvenanceReader(SlabFile slab)
    {
        Symbols = SymbolTable.Read(slab);
        Allocations = slab.GetColumn<AllocationRecord>(SectionType.Allocations);
        _objectAddresses = slab.GetColumn<ulong>(SectionType.ObjectAddresses);
        _objectStacks = slab.GetColumn<uint>(SectionType.ObjectStacks);

        // Every sum Sherlock computes is part of these totals, so bounding them keeps all arithmetic exact.
        ulong bytes = 0, count = 0, survivedBytes = 0, survivedCount = 0;
        for (long i = 0; i < Allocations.Length; i++)
        {
            AllocationRecord allocation = Allocations[i];
            if (allocation.StackId >= Symbols.StackCount)
            {
                throw new InvalidDataException("allocation references an unknown stack");
            }
            if (allocation.TypeId >= Symbols.Types.Count)
            {
                throw new InvalidDataException("allocation references an unknown type");
            }
            bytes = Total(bytes, allocation.AllocBytes);
            count = Total(count, allocation.AllocCount);
            survivedBytes = Total(survivedBytes, allocation.SurvivedBytes);
            survivedCount = Total(survivedCount, allocation.SurvivedCount);
        }

        if (_objectStacks.Length != _objectAddresses.Length)
        {
            throw new InvalidDataException("live objects and their stacks differ in number");
        }
        for (long i = 0; i < _objectAddresses.Length; i++)
        {
            if (_objectStacks[i] >= Symbols.StackCount)
            {
                throw new InvalidDataException("a live object references an unknown stack");
            }
            if (i > 0 && _objectAddresses[i] <= _objectAddresses[i - 1])
            {
                throw new InvalidDataException("live object addresses are not strictly sorted");
            }
        }
    }

    private static ulong Total(ulong total, ulong value) =>
        value > (ulong)long.MaxValue - total ? throw new InvalidDataException("allocation totals are too large to count") : total + value;

    public static void ValidateFile(string path)
    {
        using SlabFile slab = ProfileFormat.Open(path);
        _ = new ProvenanceReader(slab);
    }

    /// <summary>The number of tracked live objects.</summary>
    public long CorrelationCount => _objectAddresses.Length;

    /// <summary>Binary-searches the sorted object addresses for an object's allocating stack id.</summary>
    public bool TryGetStack(ulong address, out uint stackId)
    {
        long lo = 0, hi = _objectAddresses.Length - 1;
        while (lo <= hi)
        {
            long mid = lo + ((hi - lo) >> 1);
            ulong a = _objectAddresses[mid];
            if (a == address)
            {
                stackId = _objectStacks[mid];
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

    /// <summary>Explains an empty <see cref="FramesFor"/> stack.</summary>
    public const string NoManagedFrames =
        "Allocated by the runtime with no managed code on the stack (startup, statics, or a runtime helper).";

    /// <summary>The allocation stack of a live object, root->leaf; empty when it was allocated without managed frames,
    /// null when the object is untracked.</summary>
    public IReadOnlyList<MethodFrame>? FramesFor(ulong address) => TryGetStack(address, out uint stackId) ? Symbols.Stack(stackId) : null;
}
