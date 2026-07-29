using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.HeapModel;

/// <summary>
/// Builds a <see cref="HeapGraph"/> from a dump, bypassing ClrMD's per-object cost. ClrMD's DAC
/// (mscordaccore) takes a giant lock and drives <c>EnumerateObjects</c>/<c>EnumerateReferences</c> at
/// ~1-2M edges/s single-threaded; here we use ClrMD only for the cheap O(types + segments) metadata
/// (each type's size layout + GC descriptor, the segment ranges, the allocation-context gaps, and the
/// roots) and do the O(objects + edges) walk ourselves over raw memory:
/// <list type="bullet">
/// <item>the object walk mirrors <c>ClrHeap.EnumerateObjects</c> exactly (method-table read, size from
/// the cached type, alignment, allocation-context skip) but reads raw bytes via <see cref="IMemoryReader"/>;</item>
/// <item>the reference walk applies each type's cached <see cref="GCDesc"/> to the raw object bytes, in
/// parallel across object ranges — raw reads are not under the DAC lock (the reader reports itself
/// thread-safe), so this scales across cores.</item>
/// </list>
/// </summary>
public sealed class HeapGraphExtractor(DumpSession session)
{
    private const int MaxWorkers = 16;

    // Edges per on-disk/in-memory chunk. 1<<27 ints = 512 MB — comfortably under the int.MaxValue
    // element / ~2 GB byte cap on a single array and slab section, so a chunk always fits both.
    private const long MaxEdgesPerChunk = 1L << 27;

    public HeapGraph Extract(CancellationToken cancellationToken = default)
    {
        ClrHeap heap = session.Runtime.Heap;
        IMemoryReader reader = session.DataTarget.DataReader;
        int pointerSize = reader.PointerSize;
        uint minObjSize = (uint)(pointerSize * 3);
        
        static ulong Align(ulong size) => (size + 7) & ~7UL; // object alignment is 8 on 64-bit

        // --- Metadata (ClrMD, once): allocation-context gaps, address-ordered segments, per-type layout. ---
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

        // --- 1. Object walk (raw): dense ids in address order, shallow sizes, and each object's type. ---
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
                    break;
                }
                int typeIndex = types.IndexOf(mtRaw & ~7UL);
                ref readonly TypeInfo t = ref types[typeIndex];
                if (t.BaseSize == 0)
                {
                    break; // unresolvable method table — stop this segment
                }

                ulong size;
                if (t.ComponentSize == 0)
                {
                    size = (uint)t.BaseSize;
                }
                else
                {
                    reader.Read(obj + (ulong)pointerSize, out uint count);
                    if (t.IsString) count++;
                    size = (ulong)count * (ulong)t.ComponentSize + (ulong)t.BaseSize;
                }
                if (size < minObjSize) size = minObjSize;

                // The object's *reported* size (matches ClrMD's GetObjectSize: clamped, NOT aligned).
                // Alignment is applied only to advance the walk to the next object, not stored as size.
                ulong reportedSize = size;

                if (!t.IsFree)
                {
                    addressList.Add(obj);
                    sizeList.Add((uint)Math.Min(reportedSize, uint.MaxValue));
                    typeList.Add(typeIndex);
                }
                else
                {
                    freeBytes += reportedSize; // free-space total (fragmentation), not a real object
                    freeCount++;
                }

                obj += Align(size);
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

        // --- 2. Reference walk (raw, parallel): apply each type's GCDesc to raw object bytes. ---
        int workers = session.DataTarget.DataReader.IsThreadSafe 
            ? Math.Clamp(Environment.ProcessorCount, 1, MaxWorkers) 
            : 1;
        
        var blockDegrees = new int[workers][];
        var blockEdges = new int[workers][];
        Parallel.For(0, workers, w =>
        {
            int lo = (int)((long)n * w / workers), hi = (int)((long)n * (w + 1) / workers);
            var degrees = new int[hi - lo];
            var edges = new List<int>((hi - lo) * 3);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
            for (int i = lo; i < hi; i++)
            {
                ref readonly TypeInfo t = ref types[typeOf[i]];
                if (!t.HasPointers) { degrees[i - lo] = 0; continue; }

                uint size = sizes[i];
                if (size > buffer.Length)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent((int)size);
                }
                
                if (reader.Read(addresses[i], buffer.AsSpan(0, (int)size)) < (int)size)
                {
                    degrees[i - lo] = 0;
                    continue;
                }

                int degree = 0;
                foreach ((ulong reference, int _) in t.GCDesc.WalkObject(buffer, (int)size))
                {
                    if (reference == 0) continue;
                    int v = index.IndexOf(reference);
                    if (v >= 0) { edges.Add(v); degree++; }
                }
                
                degrees[i - lo] = degree;
            }
            
            ArrayPool<byte>.Shared.Return(buffer);
            blockDegrees[w] = degrees;
            blockEdges[w] = edges.ToArray();
        });

        // --- 3. Assemble CSR (+ the synthetic root -> GC roots). ---
        var roots = new List<int>();
        var rootSeen = new HashSet<int>();
        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            int v = index.IndexOf(root.Object.Address);
            if (v >= 0 && rootSeen.Add(v)) roots.Add(v);
        }

        long objectEdges = 0;
        foreach (int[] e in blockEdges) objectEdges += e.Length;

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

        // The edge column, node-aligned into ≤~1 GB chunks so it can exceed the 2.1B single-array ceiling.
        // Segments are the per-worker edge blocks in id order, then the synthetic root's GC-root edges.
        var edgeSegments = new List<ReadOnlyMemory<int>>(workers + 1);
        foreach (int[] e in blockEdges) edgeSegments.Add(e);
        edgeSegments.Add(roots.ToArray());
        var edges = EdgeColumn.Build(edgeSegments, offsets, MaxEdgesPerChunk);

        return new HeapGraph(addresses, sizes, offsets, edges, (ReadOnlyMemory<int>)typeOf, types.Names(), freeBytes, freeCount);
    }

    /// <summary>Cached per-type layout keyed by method table: the size formula inputs, the type name,
    /// and the GC descriptor used to find reference fields. Resolved from ClrMD once per distinct type.</summary>
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

        /// <summary>The distinct type names, indexed the same as the per-object type ids (<see cref="IndexOf"/>).</summary>
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
