using System;
using System.Buffers;
using System.Collections.Generic;
using Sherlock.Core.Storage;

namespace Sherlock.Core.HeapModel;

/// <summary>A long-indexed edge column: one mapped section for a loaded graph, or one array for a small graph made in
/// memory, as in tests.</summary>
public sealed class EdgeColumn
{
    private readonly Column<int>? _mapped;
    private readonly ReadOnlyMemory<int> _memory;

    /// <summary>Edges read from a mapped section, which the graph keeps open.</summary>
    public EdgeColumn(Column<int> mapped) => _mapped = mapped;

    public EdgeColumn(ReadOnlyMemory<int> edges) => _memory = edges;

    /// <summary>Total number of edges.</summary>
    public long Count => _mapped?.Length ?? _memory.Length;

    /// <summary>Copies the edges into one array, for columns that fit one.</summary>
    public int[] ToArray()
    {
        if (_mapped is not { } mapped)
        {
            return _memory.ToArray();
        }
        var all = new int[checked((int)mapped.Length)];
        mapped.CopyTo(0, all);
        return all;
    }

    /// <summary>The edges as consecutive runs, for writing them out as one section.</summary>
    public IReadOnlyList<ReadOnlyMemory<int>> Parts()
    {
        if (_mapped is not { } mapped)
        {
            return [_memory];
        }
        const int maxRun = 1 << 28;
        var parts = new List<ReadOnlyMemory<int>>();
        for (long start = 0; start < mapped.Length; start += maxRun)
        {
            parts.Add(mapped.AsMemory(start, (int)Math.Min(maxRun, mapped.Length - start)));
        }
        return parts;
    }

    /// <summary>Returns a range that lies within one CSR row.</summary>
    public ReadOnlySpan<int> Slice(long start, int length) =>
        _mapped is { } mapped ? mapped.Slice(start, length) : _memory.Span.Slice(checked((int)start), length);

    /// <summary>The first edge, for walks over every row; valid while <paramref name="pin"/> is held and the graph is
    /// open.</summary>
    internal unsafe int* Pin(out MemoryHandle pin)
    {
        if (_mapped is { } mapped)
        {
            pin = default;
            return mapped.Pointer;
        }
        pin = _memory.Pin();
        return (int*)pin.Pointer;
    }
}
