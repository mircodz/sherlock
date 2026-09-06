using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.HeapModel;

/// <summary>
/// Builds a graph from raw dump memory using ClrMD type layouts, GC descriptors, segments, allocation
/// contexts and roots. The object walk preserves ClrMD sizing and alignment; reference extraction
/// runs in parallel only when the memory reader is thread-safe.
/// </summary>
public sealed class HeapGraphExtractor(Snapshot snapshot)
{
    private const int MaxWorkers = 16;

    // 512 MiB of int edges, within managed array and byte-span limits.
    private const long MaxEdgesPerChunk = 1L << 27;

    public HeapGraph Extract(CancellationToken cancellationToken = default)
    {
        ClrHeap heap = snapshot.Runtime.Heap;
        IMemoryReader reader = snapshot.DataTarget.DataReader;
        int pointerSize = reader.PointerSize;
        uint minObjSize = (uint)(pointerSize * 3);

        static ulong Align(ulong size) => (size + 7) & ~7UL; // object alignment is 8 on 64-bit

        var allocContexts = new Dictionary<ulong, ulong>();
        foreach (MemoryRange r in heap.EnumerateAllocationContexts())
        {
            allocContexts[r.Start] = r.End;
        }

        ClrSegment[] segments = heap.Segments
            .Where(s => s.ObjectRange.Length > 0)
            .OrderBy(s => s.ObjectRange.Start)
            .ToArray();

        var types = new TypeTable(heap);

        // Assign dense ids in address order.
        var addressList = new List<ulong>();
        var sizeList = new List<uint>();
        var typeList = new List<int>();
        ulong freeBytes = 0;
        long freeCount = 0;
        foreach (ClrSegment seg in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool skipContexts = seg.Kind != GCSegmentKind.Large && seg.Kind != GCSegmentKind.Frozen;
            ulong obj = seg.ObjectRange.Start;
            while (obj != 0 && seg.ObjectRange.Contains(obj))
            {
                if (!reader.ReadPointer(obj, out ulong mtRaw) || mtRaw == 0)
                {
                    throw new InvalidDataException($"Could not read an object method table at 0x{obj:x}.");
                }
                int typeIndex = types.IndexOf(mtRaw & ~7UL);
                ref readonly TypeInfo t = ref types[typeIndex];
                if (t.BaseSize <= 0)
                {
                    throw new InvalidDataException($"Could not resolve method table 0x{mtRaw & ~7UL:x} at object 0x{obj:x}.");
                }

                ulong size;
                if (t.ComponentSize == 0)
                {
                    size = (uint)t.BaseSize;
                }
                else
                {
                    if (!reader.Read(obj + (ulong)pointerSize, out uint count))
                    {
                        throw new InvalidDataException($"Could not read the component count at object 0x{obj:x}.");
                    }
                    ulong componentCount = t.IsString ? (ulong)count + 1 : count;
                    size = checked(componentCount * (ulong)t.ComponentSize + (ulong)t.BaseSize);
                }
                if (size < minObjSize)
                {
                    size = minObjSize;
                }

                // Store ClrMD's clamped, unaligned size; alignment only advances the walk.
                if (!t.IsFree)
                {
                    if (size > uint.MaxValue)
                    {
                        throw new NotSupportedException($"Object 0x{obj:x} is larger than the 4 GB graph-size limit.");
                    }
                    if (t.HasPointers && size > int.MaxValue)
                    {
                        throw new NotSupportedException($"Object 0x{obj:x} is too large for reference extraction.");
                    }
                    addressList.Add(obj);
                    sizeList.Add((uint)size);
                    typeList.Add(typeIndex);
                }
                else
                {
                    freeBytes += size;
                    freeCount++;
                }

                obj = checked(obj + Align(size));
                if (skipContexts)
                {
                    while (allocContexts.TryGetValue(obj, out ulong end))
                    {
                        obj = end + Align(minObjSize);
                        if (obj >= seg.ObjectRange.End) { obj = 0; break; }
                    }
                }
            }
        }

        ulong[] addresses = addressList.ToArray();
        uint[] sizes = sizeList.ToArray();
        int[] typeOf = typeList.ToArray();
        int n = addresses.Length;
        var index = new AddressIndex(addresses);

        // Apply cached GC descriptors to raw object bytes.
        int workers = snapshot.DataTarget.DataReader.IsThreadSafe
            ? Math.Clamp(Environment.ProcessorCount, 1, MaxWorkers)
            : 1;

        var blockDegrees = new int[workers][];
        var blockEdges = new int[workers][];
        Parallel.For(0, workers, new ParallelOptions { CancellationToken = cancellationToken }, w =>
        {
            int lo = (int)((long)n * w / workers), hi = (int)((long)n * (w + 1) / workers);
            var degrees = new int[hi - lo];
            var edges = new List<int>((hi - lo) * 3);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                for (int i = lo; i < hi; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ref readonly TypeInfo t = ref types[typeOf[i]];
                    if (!t.HasPointers)
                    {
                        continue;
                    }

                    uint size = sizes[i];
                    if (size > buffer.Length)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = ArrayPool<byte>.Shared.Rent((int)size);
                    }

                    if (reader.Read(addresses[i], buffer.AsSpan(0, (int)size)) < (int)size)
                    {
                        throw new InvalidDataException($"Could not read object 0x{addresses[i]:x}.");
                    }

                    int degree = 0;
                    foreach ((ulong reference, int _) in t.GCDesc.WalkObject(buffer, (int)size))
                    {
                        if (reference == 0)
                        {
                            continue;
                        }

                        int v = index.IndexOf(reference);
                        if (v < 0)
                        {
                            throw new InvalidDataException($"Object 0x{addresses[i]:x} references unknown object 0x{reference:x}.");
                        }
                        edges.Add(v);
                        degree++;
                    }
                    degrees[i - lo] = degree;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            blockDegrees[w] = degrees;
            blockEdges[w] = edges.ToArray();
        });

        // Preserve every root record, but deduplicate the synthetic root's successor ids.
        var roots = new List<int>();
        var rootRecords = new List<HeapRootRecord>();
        var rootSeen = new HashSet<int>();
        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong address = root.Object.Address;
            if (address == 0)
            {
                continue;
            }
            int v = index.IndexOf(address);
            if (v < 0)
            {
                throw new InvalidDataException($"GC root references unknown object 0x{address:x}.");
            }
            rootRecords.Add(new HeapRootRecord(v, root.Address, root.RootKind, root.IsInterior, root.IsPinned));
            if (rootSeen.Add(v))
            {
                roots.Add(v);
            }
        }

        var offsets = new long[n + 2];
        long edge = 0;
        int node = 0;
        for (int w = 0; w < workers; w++)
        {
            foreach (int degree in blockDegrees[w])
            {
                offsets[node++] = edge;
                edge += degree;
            }
        }
        offsets[n] = edge; // synthetic root's edges start here
        offsets[n + 1] = edge + roots.Count;

        // Chunk at node boundaries: worker edges in id order, then synthetic-root edges.
        var edgeSegments = new List<ReadOnlyMemory<int>>(workers + 1);
        foreach (int[] e in blockEdges) edgeSegments.Add(e);
        edgeSegments.Add(roots.ToArray());
        var edges = EdgeColumn.Build(edgeSegments, offsets, MaxEdgesPerChunk);

        return new HeapGraph(addresses, sizes, offsets, edges, typeOf, types.Names(), freeBytes, freeCount, rootRecords.ToArray());
    }

    // Resolved from ClrMD once per method table.
    private readonly struct TypeInfo(int baseSize, int componentSize, bool isString, bool isFree, GCDesc gcDesc, bool hasPointers, string name)
    {
        public readonly int BaseSize = baseSize;
        public readonly int ComponentSize = componentSize;
        public readonly bool IsString = isString;
        public readonly bool IsFree = isFree;
        public readonly GCDesc GCDesc = gcDesc;
        public readonly bool HasPointers = hasPointers;
        public readonly string Name = name;
    }

    private sealed class TypeTable(ClrHeap heap)
    {
        private readonly Dictionary<ulong, int> _indexOfMt = new();
        private readonly List<TypeInfo> _types = new();

        public ref readonly TypeInfo this[int index] => ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_types)[index];

        public string[] Names()
        {
            var names = new string[_types.Count];
            for (int i = 0; i < _types.Count; i++) names[i] = _types[i].Name;
            return names;
        }

        public int IndexOf(ulong methodTable)
        {
            if (_indexOfMt.TryGetValue(methodTable, out int i))
            {
                return i;
            }

            i = _types.Count;
            ClrType? type = heap.GetTypeByMethodTable(methodTable);
            if (type is null)
            {
                _types.Add(new TypeInfo(0, 0, false, true, default, false, "<unknown>"));
            }
            else
            {
                bool hasPointers = type.ContainsPointers && !type.GCDesc.IsEmpty;
                _types.Add(new TypeInfo(type.StaticSize, type.ComponentSize, type.IsString, type.IsFree,
                    hasPointers ? type.GCDesc : default, hasPointers, type.Name ?? "<unknown>"));
            }
            _indexOfMt[methodTable] = i;
            return i;
        }
    }
}
