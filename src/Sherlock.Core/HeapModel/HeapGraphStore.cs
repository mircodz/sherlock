using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Sherlock.Core.Storage;

namespace Sherlock.Core.HeapModel;

/// <summary>
/// Round-trips a <see cref="HeapGraph"/> to a self-describing <c>.slab</c> container: the columns are
/// written as zero-copy POD sections and read back as zero-copy views into the memory-mapped file, so a
/// persisted graph reloads without re-walking the dump and without materializing every column into a
/// heap array (which would double peak memory). The reloaded graph owns the mapping and frees it on
/// dispose. The optional type columns (per-object type id + the name table) are written when present,
/// and absent-on-load simply yields a graph with no types (callers fall back to ClrMD).
///
/// The edge column is written as one <see cref="SectionType.GraphEdgesChunk"/> section per
/// <see cref="EdgeColumn"/> chunk (plus a <see cref="SectionType.GraphEdgeChunkMeta"/> chunk-start
/// table), and the whole file is streamed — so a graph whose edges exceed the ~2.1B single-array /
/// single-section ceiling round-trips without ever concatenating them. CSR offsets are <c>long</c>.
/// </summary>
public static class HeapGraphStore
{
    // Bump when the section set or layout changes. Load rejects any slab whose core section carries a
    // different version, so an out-of-date sidecar is transparently rebuilt rather than read partially.
    // v2: added the TypeIds/TypeNames columns and the GraphMeta (free-space) section.
    // v3: long CSR offsets + chunked edge column (GraphEdgesChunk / GraphEdgeChunkMeta).
    // v4: per-object columns (addresses/sizes/offsets/typeids) written chunked so a >268M-object graph
    //     no longer overflows the writer's int-length span or the reader's per-section cap.
    private const ushort Version = 4;

    public static void Save(string path, HeapGraph graph)
    {
        var writer = new ContainerWriter();
        // Per-object columns are chunked (N same-typed sections of uniform element count): a large graph
        // (>~268M objects) overflows a single AddRecords section. Column<T> reassembles them on load.
        writer.AddChunkedRecords<ulong>(SectionType.GraphAddresses, Version, graph.Addresses.Span);
        writer.AddChunkedRecords<uint>(SectionType.GraphSizes, Version, graph.Sizes.Span);
        writer.AddChunkedRecords<long>(SectionType.GraphOffsets, Version, graph.Offsets.Span);

        // One section per edge chunk (each already ≤ the on-disk section cap), plus the chunk-start table
        // so the reader rebuilds the column without re-deriving boundaries from the offsets.
        EdgeColumn edges = graph.Edges;
        IReadOnlyList<ReadOnlyMemory<byte>> edgeChunks = edges.ChunksAsBytes();
        for (int i = 0; i < edgeChunks.Count; i++)
        {
            writer.AddChunkedSection(SectionType.GraphEdgesChunk, Version, sizeof(int), [edgeChunks[i]],
                (ulong)(edges.ChunkStarts[i + 1] - edges.ChunkStarts[i]));
        }
        writer.AddRecords<long>(SectionType.GraphEdgeChunkMeta, Version, edges.ChunkStarts);

        writer.AddRecords<ulong>(SectionType.GraphMeta, Version, [graph.FreeBytes, (ulong)graph.FreeCount]);
        if (graph.TypeIds is { } typeIds && graph.TypeNames is { } typeNames)
        {
            writer.AddChunkedRecords<int>(SectionType.GraphTypeIds, Version, typeIds.Span);
            byte[] namesBlob = EncodeNames(typeNames);
            writer.AddSection(SectionType.GraphTypeNames, Version, 0, namesBlob, (ulong)typeNames.Length);
        }
        writer.Save(path);
    }

    /// <summary>Loads a graph from <paramref name="path"/>, or null if the file is missing a core section
    /// (a format mismatch — the caller should rebuild). Type columns are optional.
    ///
    /// The per-object columns are chunked on disk; here they're materialized into dense arrays (via
    /// <see cref="Column{T}"/>) so <see cref="HeapGraph"/> and the analyzers keep their contiguous
    /// <see cref="ReadOnlyMemory{T}"/> view and the dominator hot loops stay raw-span indexed — no
    /// out-of-core indirection. The edge column stays memory-mapped (chunked, not materialized), so the
    /// returned graph owns the <see cref="SlabFile"/> and frees it on dispose. Materializing the small
    /// per-object columns caps at ~2.1B objects (int array element count), far past any real dump.</summary>
    public static HeapGraph? Load(string path)
    {
        SlabFile slab = SlabFile.Open(path);
        try
        {
            // GraphOffsets (always ≥2 longs, incl. the synthetic root) and GraphEdgeChunkMeta are present
            // for any valid graph — including an empty one, whose GraphAddresses/GraphSizes columns are
            // legitimately zero-length (and so emit no sections). Gate validity/version on GraphOffsets.
            if (!slab.Has(SectionType.GraphOffsets) || !slab.Has(SectionType.GraphEdgeChunkMeta))
            {
                slab.Dispose();
                return null;
            }
            if (slab.SectionVersion(SectionType.GraphOffsets) != Version)
            {
                slab.Dispose();
                return null; // stale format — caller rebuilds from the dump
            }

            ReadOnlyMemory<ulong> addresses = Materialize(slab.GetColumn<ulong>(SectionType.GraphAddresses));
            ReadOnlyMemory<uint> sizes = Materialize(slab.GetColumn<uint>(SectionType.GraphSizes));
            ReadOnlyMemory<long> offsets = Materialize(slab.GetColumn<long>(SectionType.GraphOffsets));

            // The edge column stays mmap-backed: each chunk section is a zero-copy view, reassembled
            // (with the chunk-start table) into an EdgeColumn that Successors slices directly.
            long[] chunkStarts = MaterializeArray(slab.GetColumn<long>(SectionType.GraphEdgeChunkMeta));
            IReadOnlyList<Column<int>> edgeSections = slab.SectionColumns<int>(SectionType.GraphEdgesChunk);
            var chunks = new ReadOnlyMemory<int>[edgeSections.Count];
            for (int i = 0; i < chunks.Length; i++)
            {
                chunks[i] = edgeSections[i].AsMemory();
            }
            var edges = new EdgeColumn(chunks, chunkStarts);

            ReadOnlyMemory<int>? typeIds = null;
            string[]? typeNames = null;
            if (slab.Has(SectionType.GraphTypeIds) && slab.Has(SectionType.GraphTypeNames))
            {
                typeIds = Materialize(slab.GetColumn<int>(SectionType.GraphTypeIds));
                typeNames = DecodeNames(slab.Blob(SectionType.GraphTypeNames));
            }

            ulong freeBytes = 0;
            long freeCount = 0;
            Column<ulong> meta = slab.GetColumn<ulong>(SectionType.GraphMeta);
            if (meta.Length >= 1) freeBytes = meta[0];
            if (meta.Length >= 2) freeCount = (long)meta[1];

            return new HeapGraph(addresses, sizes, offsets, edges, typeIds, typeNames, freeBytes, freeCount,
                backing: slab);
        }
        catch
        {
            slab.Dispose();
            throw;
        }
    }

    // Materializes a chunked column into a dense array (the common, sub-2.1B case). Keeps HeapGraph's
    // contiguous ReadOnlyMemory<T> view so the analyzers and dominator hot loops are unchanged.
    private static ReadOnlyMemory<T> Materialize<T>(Column<T> col) where T : unmanaged =>
        MaterializeArray(col);

    private static T[] MaterializeArray<T>(Column<T> col) where T : unmanaged
    {
        var arr = GC.AllocateUninitializedArray<T>(checked((int)col.Length));
        col.CopyTo(0, arr);
        return arr;
    }

    // [u32 count][ (u32 byteLen, utf8 bytes) x count ] — little-endian.
    private static byte[] EncodeNames(string[] names)
    {
        var buffer = new List<byte>(names.Length * 24);
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)names.Length);
        buffer.AddRange(u32);
        foreach (string name in names)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(name);
            BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)utf8.Length);
            buffer.AddRange(u32);
            buffer.AddRange(utf8);
        }
        return buffer.ToArray();
    }

    private static string[] DecodeNames(ReadOnlySpan<byte> blob)
    {
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob);
        var names = new string[count];
        int pos = 4;
        for (int i = 0; i < count; i++)
        {
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[pos..]);
            pos += 4;
            names[i] = Encoding.UTF8.GetString(blob.Slice(pos, len));
            pos += len;
        }
        return names;
    }
}
