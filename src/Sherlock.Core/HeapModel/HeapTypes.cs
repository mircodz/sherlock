using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.HeapModel;

/// <summary>What a walk needs from a method table: how to size its objects, whether they're free space, and where
/// their references are.</summary>
internal sealed class HeapType
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required ulong BaseSize { get; init; }
    public required ulong ComponentSize { get; init; }
    public required bool IsString { get; init; }
    public required bool IsFree { get; init; }
    public GCDescriptor? References { get; init; }
}

/// <summary>Method tables resolved through ClrMD, once each, shared by every walk over one dump.</summary>
internal sealed class TypeTable(ClrHeap heap, IMemoryReader reader)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, HeapType?> _byMethodTable = [];
    private readonly List<HeapType> _types = [];

    /// <summary>The type of <paramref name="methodTable"/>, or null when ClrMD can't resolve it.</summary>
    public HeapType? Resolve(ulong methodTable)
    {
        lock (_gate)
        {
            if (_byMethodTable.TryGetValue(methodTable, out HeapType? known))
            {
                return known;
            }
            ClrType? clr = heap.GetTypeByMethodTable(methodTable);
            HeapType? type = clr is null || clr.StaticSize <= 0 ? null : new HeapType
            {
                Index = _types.Count,
                Name = clr.Name ?? "<unknown>",
                BaseSize = (ulong)clr.StaticSize,
                ComponentSize = (ulong)clr.ComponentSize,
                IsString = clr.IsString,
                IsFree = clr.IsFree,
                References = clr.ContainsPointers ? GCDescriptor.Read(reader, methodTable) : null,
            };
            if (type is not null)
            {
                _types.Add(type);
            }
            _byMethodTable[methodTable] = type;
            return type;
        }
    }

    /// <summary>The types resolved so far, by index; every index a finished walk handed out is in it.</summary>
    public HeapType[] ToArray()
    {
        lock (_gate)
        {
            return [.. _types];
        }
    }
}

/// <summary>One worker's method table lookups, in front of the shared table.</summary>
internal sealed class TypeCache(TypeTable table)
{
    private ulong _lastMethodTable;
    private HeapType? _last;
    private ulong[] _keys = new ulong[256];
    private HeapType?[] _values = new HeapType?[256];
    private int _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public HeapType Get(ulong methodTable, ulong address) => methodTable == _lastMethodTable ? _last! : Find(methodTable, address);

    private HeapType Find(ulong methodTable, ulong address)
    {
        int mask = _keys.Length - 1;
        int i = Hash(methodTable) & mask;
        for (ulong key; (key = _keys[i]) != 0; i = (i + 1) & mask)
        {
            if (key == methodTable)
            {
                _lastMethodTable = methodTable;
                return _last = _values[i]!;
            }
        }
        HeapType type = table.Resolve(methodTable) ?? throw new InvalidDataException($"Could not resolve method table 0x{methodTable:x} at object 0x{address:x}.");
        _keys[i] = methodTable;
        _values[i] = type;
        if (++_count * 2 > _keys.Length)
        {
            Grow();
        }
        _lastMethodTable = methodTable;
        return _last = type;
    }

    private static int Hash(ulong key) => (int)(((key >> 3) * 0x9E3779B97F4A7C15UL) >> 33);

    private void Grow()
    {
        ulong[] keys = _keys;
        HeapType?[] values = _values;
        _keys = new ulong[keys.Length * 2];
        _values = new HeapType?[keys.Length * 2];
        int mask = _keys.Length - 1;
        for (int j = 0; j < keys.Length; j++)
        {
            if (keys[j] == 0)
            {
                continue;
            }
            int i = Hash(keys[j]) & mask;
            while (_keys[i] != 0)
            {
                i = (i + 1) & mask;
            }
            _keys[i] = keys[j];
            _values[i] = values[j];
        }
    }
}

internal interface IReferenceVisitor
{
    void Reference(ulong target);
}

/// <summary>
/// A method table's GC descriptor: where its objects' references are. Decoded once per type and applied to object
/// bytes in place, it visits references in the order ClrMD's <c>GCDesc.WalkObject</c> yields them: series from the
/// highest down, or, for arrays of structs, each element's runs of references and skipped bytes.
/// </summary>
internal sealed unsafe class GCDescriptor
{
    private readonly long[] _offsets = [];
    private readonly long[] _sizes = [];
    private readonly bool _repeats;
    private readonly long _start;
    private readonly int[] _runs = [];
    private readonly int[] _skips = [];

    private GCDescriptor(long[] offsets, long[] sizes)
    {
        _offsets = offsets;
        _sizes = sizes;
    }

    private GCDescriptor(long start, int[] runs, int[] skips)
    {
        _repeats = true;
        _start = start;
        _runs = runs;
        _skips = skips;
    }

    /// <summary>Reads the descriptor stored below the method table, as ClrMD does; null when it can't be read.</summary>
    public static GCDescriptor? Read(IMemoryReader reader, ulong methodTable)
    {
        if (!reader.Read(methodTable - 8, out int entries))
        {
            return null;
        }
        entries = entries < 0 ? -entries : entries;
        if (entries <= 0 || entries > 1 << 20)
        {
            throw Malformed(methodTable);
        }
        var data = new byte[(1 + entries * 2) * 8];
        return reader.Read(methodTable - (ulong)data.Length, data) == data.Length ? Decode(data, methodTable) : null;
    }

    /// <summary>Decodes the 8 * (1 + 2 * series) bytes below <paramref name="methodTable"/>: the series, then their count.</summary>
    public static GCDescriptor Decode(ReadOnlySpan<byte> data, ulong methodTable)
    {
        int series = (int)MemoryMarshal.Read<long>(data[^8..]);
        int highest = data.Length - 24;
        if (series > 0)
        {
            int lowest = data.Length - (8 + series * 16);
            if (lowest < 0)
            {
                throw Malformed(methodTable);
            }
            var offsets = new long[series];
            var sizes = new long[series];
            for (int s = 0, at = highest; at >= lowest; s++, at -= 16)
            {
                sizes[s] = (int)MemoryMarshal.Read<long>(data[at..]);
                offsets[s] = MemoryMarshal.Read<long>(data[(at + 8)..]);
            }
            return new GCDescriptor(offsets, sizes);
        }

        int items = -series;
        if (items <= 0 || highest - (items - 1) * 8 < 0)
        {
            throw Malformed(methodTable);
        }
        var runs = new int[items];
        var skips = new int[items];
        for (int k = 0; k < items; k++)
        {
            runs[k] = MemoryMarshal.Read<int>(data[(highest - k * 8)..]);
            skips[k] = MemoryMarshal.Read<int>(data[(highest - k * 8 + 4)..]);
        }
        return new GCDescriptor(MemoryMarshal.Read<long>(data[(highest + 8)..]), runs, skips);
    }

    /// <summary>Visits each non-null reference among the object's <paramref name="size"/> bytes at <paramref name="memory"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Walk<TVisitor>(byte* memory, long size, ref TVisitor visitor) where TVisitor : struct, IReferenceVisitor
    {
        if (!_repeats)
        {
            long[] offsets = _offsets, sizes = _sizes;
            for (int s = 0; s < offsets.Length; s++)
            {
                long offset = offsets[s];
                long stop = offset + sizes[s] + size;
                if (offset < 0 || stop > size)
                {
                    throw Malformed(*(ulong*)memory);
                }
                for (; offset < stop; offset += 8)
                {
                    ulong target = *(ulong*)(memory + offset);
                    if (target != 0)
                    {
                        visitor.Reference(target);
                    }
                }
            }
            return;
        }

        long at = _start;
        int[] runs = _runs, skips = _skips;
        while (at < size - 8)
        {
            for (int i = 0; i < runs.Length; i++)
            {
                long stop = at + runs[i] * 8L;
                if (at < 0 || Math.Max(stop, at + 8) > size)
                {
                    throw Malformed(*(ulong*)memory);
                }
                // At least one slot per run, as ClrMD reads them.
                do
                {
                    ulong target = *(ulong*)(memory + at);
                    if (target != 0)
                    {
                        visitor.Reference(target);
                    }
                    at += 8;
                } while (at < stop);
                at += skips[i];
            }
        }
    }

    private static InvalidDataException Malformed(ulong methodTable) =>
        new($"The GC descriptor of method table 0x{methodTable & ~7UL:x} doesn't fit its objects.");
}
