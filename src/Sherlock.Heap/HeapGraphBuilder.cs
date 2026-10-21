using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Win32.SafeHandles;
using Sherlock.Storage;

namespace Sherlock.Heap;

/// <summary>
/// Writes a heap graph file without holding the graph in memory, in two parallel walks over the mapped dump. The
/// first counts each segment's objects and references and records where objects start in a temporary address
/// index; the second writes every column at its final offset, turning references into ids through the index.
/// Managed memory holds types, segments, roots and per-worker buffers; everything sized like the heap is in files.
/// </summary>
internal static unsafe class HeapGraphBuilder
{
    // The second walk splits segments into chunks about this long, so large segments spread across workers.
    private const ulong ChunkBytes = 16UL << 20;

    /// <summary>Writes the graph of <paramref name="heap"/> to <paramref name="path"/>, replacing any file there once complete.</summary>
    public static void Build(DumpHeap heap, string dumpPath, string path, CancellationToken cancellationToken) =>
        Build(heap, dumpPath, path, ChunkBytes, cancellationToken);

    internal static void Build(DumpHeap heap, string dumpPath, string path, ulong chunkBytes, CancellationToken cancellationToken)
    {
        // ClrMD walks stacks and handles on its own thread while the heap is walked; a failure stops it too.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<List<Root>> roots = Task.Factory.StartNew(() => Roots(heap.Heap, stop.Token), stop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Write(heap, dumpPath, path, chunkBytes, roots, cancellationToken);
        }
        finally
        {
            stop.Cancel();
            Task.WaitAny(roots); // the caller may dispose ClrMD once this returns
        }
    }

    private static void Write(DumpHeap heap, string dumpPath, string path, ulong chunkBytes, Task<List<Root>> rootsTask, CancellationToken cancellationToken)
    {
        HeapSegment[] segments = heap.Segments;
        using var index = new AddressIndex($"{path}.{Guid.NewGuid():N}.index", segments);
        Counted[] counted = Count(heap, index, chunkBytes, cancellationToken);

        long nodes = 0, edges = 0, freeCount = 0;
        ulong freeBytes = 0;
        var nodeBases = new long[segments.Length];
        var edgeBases = new long[segments.Length];
        for (int s = 0; s < segments.Length; s++)
        {
            nodeBases[s] = nodes;
            edgeBases[s] = edges;
            nodes += counted[s].Objects;
            edges += counted[s].Edges;
            freeCount += counted[s].FreeCount;
            freeBytes += counted[s].FreeBytes;
        }
        if (nodes >= int.MaxValue)
        {
            throw new NotSupportedException($"The heap holds {nodes:N0} objects; a graph holds at most {int.MaxValue - 1:N0}.");
        }
        AddressIndex.Lookup lookup = index.Open(nodeBases);

        HeapType[] types = heap.Types.ToArray();
        var strings = new MemoryStream();
        var typeRecords = new TypeRecord[types.Length];
        for (int t = 0; t < types.Length; t++)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(types[t].Name);
            typeRecords[t] = new TypeRecord { Name = new StringRef { Offset = checked((uint)strings.Length), Length = (uint)utf8.Length } };
            strings.Write(utf8);
        }
        var large = new List<LargeSize>();
        for (int s = 0; s < segments.Length; s++)
        {
            foreach ((long local, ulong size) in counted[s].Large)
            {
                large.Add(new LargeSize((int)(nodeBases[s] + local), size));
            }
        }

        using var file = new SlabBuilder(path, sectionCount: 10);
        (long sourceLength, long sourceModified) = HeapGraphStore.SourceStamp(dumpPath);
        GraphMeta[] meta = [new GraphMeta(Guid.NewGuid(), freeBytes, freeCount, sourceLength, sourceModified)];
        file.Write(MemoryMarshal.AsBytes(meta.AsSpan()), file.Place(SectionType.GraphMeta, sizeof(GraphMeta), 1));
        file.Write(strings.GetBuffer().AsSpan(0, (int)strings.Length), file.Place(SectionType.Strings, 1, strings.Length));
        file.Write(MemoryMarshal.AsBytes(typeRecords.AsSpan()), file.Place(SectionType.Types, sizeof(TypeRecord), typeRecords.Length));
        file.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(large)), file.Place(SectionType.GraphLargeSizes, sizeof(LargeSize), large.Count));
        var at = new Columns(
            file.Place(SectionType.GraphAddresses, sizeof(ulong), nodes),
            file.Place(SectionType.GraphSizes, sizeof(uint), nodes),
            file.Place(SectionType.GraphTypeIds, sizeof(int), nodes),
            file.Place(SectionType.GraphOffsets, sizeof(long), nodes + 2),
            file.Next); // edges come next; their count is known once the roots are

        Chunk[] chunks = Chunk.Plan(counted, nodeBases, edgeBases);
        int next = -1;
        HeapWalk.Run(Environment.ProcessorCount, (_, stop) =>
        {
            var cache = new TypeCache(heap.Types);
            using var cursor = new DumpCursor(heap.Memory);
            var writer = new ColumnWriter(file.Handle, at);
            for (int i; (i = Interlocked.Increment(ref next)) < chunks.Length;)
            {
                Chunk chunk = chunks[i];
                writer.Begin(chunk.NodeBase, chunk.EdgeBase);
                var sink = new FillSink(writer, lookup);
                HeapWalk.Walk(segments[chunk.Segment], cursor, chunk.From, chunk.To, cache, ref sink, stop);
                writer.Flush();
                if (writer.NextId != chunk.NodeEnd || writer.NextEdge != chunk.EdgeEnd)
                {
                    throw new InvalidDataException($"The walk from 0x{chunk.From:x} found {writer.NextId - chunk.NodeBase:N0} objects and " +
                        $"{writer.NextEdge - chunk.EdgeBase:N0} references, but counted {chunk.NodeEnd - chunk.NodeBase:N0} and {chunk.EdgeEnd - chunk.EdgeBase:N0}.");
                }
            }
        }, cancellationToken);

        List<Root> roots = rootsTask.GetAwaiter().GetResult();
        var records = new HeapRootRecord[roots.Count];
        var rooted = new List<int>();
        var seen = new HashSet<int>();
        for (int i = 0; i < roots.Count; i++)
        {
            long id = lookup.IdOf(roots[i].Object);
            if (id < 0)
            {
                throw new InvalidDataException($"GC root references unknown object 0x{roots[i].Object:x}.");
            }
            records[i] = new HeapRootRecord((int)id, roots[i].Address, roots[i].Kind, roots[i].IsInterior, roots[i].IsPinned);
            if (seen.Add((int)id))
            {
                rooted.Add((int)id);
            }
        }
        long[] rootRow = [edges, edges + rooted.Count];
        file.Write(MemoryMarshal.AsBytes(rootRow.AsSpan()), at.Offsets + nodes * sizeof(long));
        file.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(rooted)), at.Edges + edges * sizeof(int));
        file.Place(SectionType.GraphEdges, sizeof(int), edges + rooted.Count);
        file.Write(MemoryMarshal.AsBytes(records.AsSpan()), file.Place(SectionType.GraphRoots, sizeof(HeapRootRecord), records.Length));
        file.Commit(SlabKind.Graph, HeapGraphStore.Version);
    }

    // The first walk: per segment, its counts and where the second walk can start; each object start goes to the index.
    private static Counted[] Count(DumpHeap heap, AddressIndex index, ulong chunkBytes, CancellationToken cancellationToken)
    {
        HeapSegment[] segments = heap.Segments;
        var counted = new Counted[segments.Length];
        int[] largestFirst = [.. Enumerable.Range(0, segments.Length).OrderByDescending(s => segments[s].Bytes)];
        int next = -1;
        HeapWalk.Run(Environment.ProcessorCount, (_, stop) =>
        {
            var types = new TypeCache(heap.Types);
            using var cursor = new DumpCursor(heap.Memory);
            using AddressIndex.Writer marks = index.CreateWriter();
            for (int i; (i = Interlocked.Increment(ref next)) < largestFirst.Length;)
            {
                int s = largestFirst[i];
                HeapSegment segment = segments[s];
                marks.Begin(s);
                var sink = new CountSink(segment.Start, counted[s] = new Counted(), marks, chunkBytes);
                HeapWalk.Walk(segment, cursor, segment.Start, ulong.MaxValue, types, ref sink, stop);
                if (marks.End() != counted[s].Objects)
                {
                    throw new InvalidOperationException($"Segment 0x{segment.Start:x} indexed a different number of objects than it counted.");
                }
            }
        }, cancellationToken);
        return counted;
    }

    private static List<Root> Roots(ClrHeap heap, CancellationToken cancellationToken)
    {
        var roots = new List<Root>();
        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong address = root.Object.Address;
            if (address != 0)
            {
                roots.Add(new Root(address, root.Address, root.RootKind, root.IsInterior, root.IsPinned));
            }
        }
        return roots;
    }

    private readonly record struct Root(ulong Object, ulong Address, ClrRootKind Kind, bool IsInterior, bool IsPinned);

    /// <summary>A segment after the first walk.</summary>
    private sealed class Counted
    {
        public long Objects;
        public long Edges;
        public long FreeCount;
        public ulong FreeBytes;

        /// <summary>Object starts where the second walk can begin, with the counts before each in the segment.</summary>
        public readonly List<(ulong Address, long Objects, long Edges)> Splits = [];

        /// <summary>Objects of 4 GiB or more, by their index in the segment.</summary>
        public readonly List<(long Local, ulong Size)> Large = [];
    }

    /// <summary>A stretch of a segment the second walk covers on its own, and the ids and edges it fills.</summary>
    private readonly record struct Chunk(int Segment, ulong From, ulong To, long NodeBase, long NodeEnd, long EdgeBase, long EdgeEnd)
    {
        public static Chunk[] Plan(Counted[] counted, long[] nodeBases, long[] edgeBases)
        {
            var chunks = new List<Chunk>();
            for (int s = 0; s < counted.Length; s++)
            {
                List<(ulong Address, long Objects, long Edges)> splits = counted[s].Splits;
                for (int k = 0; k < splits.Count; k++)
                {
                    bool last = k + 1 == splits.Count;
                    chunks.Add(new Chunk(
                        s,
                        splits[k].Address,
                        last ? ulong.MaxValue : splits[k + 1].Address,
                        nodeBases[s] + splits[k].Objects,
                        nodeBases[s] + (last ? counted[s].Objects : splits[k + 1].Objects),
                        edgeBases[s] + splits[k].Edges,
                        edgeBases[s] + (last ? counted[s].Edges : splits[k + 1].Edges)));
                }
            }
            return [.. chunks];
        }
    }

    private struct CountSink : IObjectSink, IReferenceVisitor
    {
        private readonly ulong _start;
        private readonly Counted _counted;
        private readonly AddressIndex.Writer _marks;
        private readonly ulong _chunkBytes;
        private ulong _nextSplit;

        public CountSink(ulong start, Counted counted, AddressIndex.Writer marks, ulong chunkBytes)
        {
            _start = start;
            _counted = counted;
            _marks = marks;
            _chunkBytes = chunkBytes;
            _nextSplit = start;
        }

        public void Object(ulong address, ulong size, HeapType type, DumpCursor cursor)
        {
            if (address >= _nextSplit)
            {
                _counted.Splits.Add((address, _counted.Objects, _counted.Edges));
                _nextSplit = ulong.MaxValue - address < _chunkBytes ? ulong.MaxValue : address + _chunkBytes;
            }
            _marks.Mark(address - _start);
            if (size >= uint.MaxValue)
            {
                _counted.Large.Add((_counted.Objects, size));
            }
            if (type.References is { } references)
            {
                HeapWalk.References(references, cursor, address, size, ref this);
            }
            _counted.Objects++;
        }

        public readonly void Reference(ulong target) => _counted.Edges++;

        public readonly void Free(ulong size)
        {
            _counted.FreeCount++;
            _counted.FreeBytes += size;
        }
    }

    private struct FillSink(ColumnWriter writer, AddressIndex.Lookup lookup) : IObjectSink, IReferenceVisitor
    {
        private ulong _current;

        public void Object(ulong address, ulong size, HeapType type, DumpCursor cursor)
        {
            writer.Node(address, size, type.Index);
            if (type.References is { } references)
            {
                _current = address;
                HeapWalk.References(references, cursor, address, size, ref this);
            }
        }

        public readonly void Reference(ulong target)
        {
            long id = lookup.IdOf(target);
            if (id < 0)
            {
                throw new InvalidDataException($"Object 0x{_current:x} references unknown object 0x{target:x}.");
            }
            writer.Edge((int)id);
        }

        public readonly void Free(ulong size)
        {
        }
    }

    /// <summary>File offsets of the sections the second walk fills.</summary>
    private readonly record struct Columns(long Addresses, long Sizes, long TypeIds, long Offsets, long Edges);

    /// <summary>One worker's output from the second walk: runs of each column, buffered and written at their offsets.</summary>
    private sealed class ColumnWriter(SafeFileHandle file, Columns at)
    {
        private const int NodeBatch = 32 * 1024;   // 768 KiB of node columns
        private const int EdgeBatch = 128 * 1024;  // 512 KiB of edges

        private readonly ulong[] _addresses = new ulong[NodeBatch];
        private readonly uint[] _sizes = new uint[NodeBatch];
        private readonly int[] _typeIds = new int[NodeBatch];
        private readonly long[] _offsets = new long[NodeBatch];
        private readonly int[] _edges = new int[EdgeBatch];
        private long _firstId;
        private int _nodes;
        private long _firstEdge;
        private int _edgeCount;

        public long NextId => _firstId + _nodes;
        public long NextEdge => _firstEdge + _edgeCount;

        public void Begin(long id, long edge)
        {
            _firstId = id;
            _nodes = 0;
            _firstEdge = edge;
            _edgeCount = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Node(ulong address, ulong size, int typeId)
        {
            if (_nodes == NodeBatch)
            {
                FlushNodes();
            }
            int n = _nodes++;
            _addresses[n] = address;
            _sizes[n] = size >= uint.MaxValue ? uint.MaxValue : (uint)size;
            _typeIds[n] = typeId;
            _offsets[n] = _firstEdge + _edgeCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Edge(int target)
        {
            if (_edgeCount == EdgeBatch)
            {
                FlushEdges();
            }
            _edges[_edgeCount++] = target;
        }

        public void Flush()
        {
            FlushNodes();
            FlushEdges();
        }

        private void FlushNodes()
        {
            RandomAccess.Write(file, MemoryMarshal.AsBytes(_addresses.AsSpan(0, _nodes)), at.Addresses + _firstId * sizeof(ulong));
            RandomAccess.Write(file, MemoryMarshal.AsBytes(_sizes.AsSpan(0, _nodes)), at.Sizes + _firstId * sizeof(uint));
            RandomAccess.Write(file, MemoryMarshal.AsBytes(_typeIds.AsSpan(0, _nodes)), at.TypeIds + _firstId * sizeof(int));
            RandomAccess.Write(file, MemoryMarshal.AsBytes(_offsets.AsSpan(0, _nodes)), at.Offsets + _firstId * sizeof(long));
            _firstId += _nodes;
            _nodes = 0;
        }

        private void FlushEdges()
        {
            RandomAccess.Write(file, MemoryMarshal.AsBytes(_edges.AsSpan(0, _edgeCount)), at.Edges + _firstEdge * sizeof(int));
            _firstEdge += _edgeCount;
            _edgeCount = 0;
        }
    }

    /// <summary>
    /// Object ids by address, in about 1/56 of the heap's size, in a temporary file. Each segment has a 64-byte line
    /// per 448 heap words: the number of the segment's objects before the line, then seven words of object-start
    /// bits, so a lookup reads one cache line. The first walk writes lines as it passes them; the second maps them.
    /// </summary>
    private sealed class AddressIndex : IDisposable
    {
        private const int LineWords = 448;

        private readonly HeapSegment[] _segments;
        private readonly long[] _lineOffsets;
        private readonly long[] _lineCounts;
        private readonly FileStream _file;
        private readonly long _length;
        private MemoryMappedFile? _map;
        private MemoryMappedViewAccessor? _view;

        public AddressIndex(string path, HeapSegment[] segments)
        {
            _segments = segments;
            _lineOffsets = new long[segments.Length];
            _lineCounts = new long[segments.Length];
            long lines = 0;
            for (int s = 0; s < segments.Length; s++)
            {
                _lineOffsets[s] = lines;
                _lineCounts[s] = (long)(((segments[s].Bytes - 1) >> 3) / LineWords) + 1;
                lines += _lineCounts[s];
            }
            _length = Math.Max(lines * 64, 64);
            // Unix: unlinked at once, so nothing is left behind even if the process dies. Windows deletes it on close.
            _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1,
                OperatingSystem.IsWindows() ? FileOptions.DeleteOnClose : FileOptions.None);
            if (!OperatingSystem.IsWindows())
            {
                File.Delete(path);
            }
            _file.SetLength(_length);
        }

        public Writer CreateWriter() => new(this);

        /// <summary>Maps the finished index for lookups; <paramref name="nodeBases"/> are each segment's first id.</summary>
        public Lookup Open(long[] nodeBases)
        {
            _map = MemoryMappedFile.CreateFromFile(_file, null, _length, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
            _view = _map.CreateViewAccessor(0, _length, MemoryMappedFileAccess.Read);
            byte* lines = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref lines);
            return new Lookup(_segments, nodeBases, _lineOffsets, lines);
        }

        public void Dispose()
        {
            if (_view is not null)
            {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
                _view.Dispose();
            }
            _map?.Dispose();
            _file.Dispose();
        }

        /// <summary>One worker's window of lines for the segment it walks: ranks are filled in and the lines written as
        /// the walk moves past them, so the walk's offsets must only increase.</summary>
        public sealed class Writer(AddressIndex index) : IDisposable
        {
            private const int WindowLines = 1024; // 64 KiB of lines, 3.5 MiB of heap

            private readonly ulong* _window = (ulong*)NativeMemory.AllocZeroed(WindowLines * 64);
            private int _segment;
            private long _first;
            private long _marked;
            private bool _dirty;

            public void Begin(int segment)
            {
                _segment = segment;
                _first = 0;
                _marked = 0;
                _dirty = false;
            }

            /// <summary>Marks an object <paramref name="offset"/> bytes into the segment.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Mark(ulong offset)
            {
                ulong word = offset >> 3;
                long line = (long)(word / LineWords);
                int bit = (int)(word - (ulong)line * LineWords);
                long slot = line - _first;
                if ((ulong)slot >= WindowLines)
                {
                    Flush();
                    _first = line - line % WindowLines;
                    slot = line - _first;
                }
                _window[slot * 8 + 1 + (bit >> 6)] |= 1UL << bit;
                _dirty = true;
            }

            /// <summary>Writes what's left of the segment; returns how many objects it marked.</summary>
            public long End()
            {
                Flush();
                return _marked;
            }

            private void Flush()
            {
                if (!_dirty)
                {
                    return;
                }
                long count = Math.Min(WindowLines, index._lineCounts[_segment] - _first);
                ulong* line = _window;
                for (long l = 0; l < count; l++, line += 8)
                {
                    line[0] = (ulong)_marked;
                    for (int w = 1; w < 8; w++)
                    {
                        _marked += BitOperations.PopCount(line[w]);
                    }
                }
                RandomAccess.Write(index._file.SafeFileHandle, new ReadOnlySpan<byte>(_window, (int)(count * 64)), (index._lineOffsets[_segment] + _first) * 64);
                new Span<byte>(_window, WindowLines * 64).Clear();
                _dirty = false;
            }

            public void Dispose() => NativeMemory.Free(_window);
        }

        /// <summary>Lookups over the mapped index, valid while it's open.</summary>
        public readonly struct Lookup
        {
            private readonly ulong[] _starts;
            private readonly ulong[] _ends;
            private readonly long[] _nodeBases;
            private readonly nint[] _lines;
            private readonly int[] _slots; // per span of 2^_shift bytes, the first segment that ends past its start
            private readonly ulong _base;
            private readonly int _shift;

            public Lookup(HeapSegment[] segments, long[] nodeBases, long[] lineOffsets, byte* lines)
            {
                _starts = new ulong[segments.Length];
                _ends = new ulong[segments.Length];
                _lines = new nint[segments.Length];
                for (int s = 0; s < segments.Length; s++)
                {
                    _starts[s] = segments[s].Start;
                    _ends[s] = segments[s].End;
                    _lines[s] = (nint)(lines + lineOffsets[s] * 64);
                }
                _nodeBases = nodeBases;
                _base = segments.Length > 0 ? segments[0].Start : 0;
                ulong span = segments.Length > 0 ? segments[^1].End - _base : 0;
                _shift = 20;
                while ((span >> _shift) >= 1UL << 20)
                {
                    _shift++;
                }
                _slots = new int[(int)(span >> _shift) + 1];
                for (int j = 0, s = 0; j < _slots.Length; j++)
                {
                    ulong slotStart = _base + ((ulong)j << _shift);
                    while (s < _ends.Length && _ends[s] <= slotStart)
                    {
                        s++;
                    }
                    _slots[j] = s;
                }
            }

            /// <summary>The id of the object starting at <paramref name="address"/>, or -1.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public long IdOf(ulong address)
            {
                ulong slot = (address - _base) >> _shift;
                if (address < _base || slot >= (ulong)_slots.Length)
                {
                    return -1;
                }
                ulong[] ends = _ends;
                int s = _slots[slot];
                while (s < ends.Length && ends[s] <= address)
                {
                    s++;
                }
                if (s == ends.Length || address < _starts[s] || ((address - _starts[s]) & 7) != 0)
                {
                    return -1;
                }
                ulong word = (address - _starts[s]) >> 3;
                ulong line = word / LineWords;
                int bit = (int)(word - line * LineWords);
                ulong* l = (ulong*)_lines[s] + line * 8;
                int w = bit >> 6;
                ulong bits = l[1 + w];
                ulong mask = 1UL << bit;
                if ((bits & mask) == 0)
                {
                    return -1;
                }
                long id = _nodeBases[s] + (long)l[0];
                for (int i = 1; i <= w; i++)
                {
                    id += BitOperations.PopCount(l[i]);
                }
                return id + BitOperations.PopCount(bits & (mask - 1));
            }
        }
    }
}
