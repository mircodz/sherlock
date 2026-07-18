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
    private const ushort Version = 3;

    public static void Save(string path, HeapGraph graph)
    {
        var writer = new ContainerWriter();
        writer.AddRecords<ulong>(SectionType.GraphAddresses, Version, graph.Addresses.Span);
        writer.AddRecords<uint>(SectionType.GraphSizes, Version, graph.Sizes.Span);
        writer.AddRecords<long>(SectionType.GraphOffsets, Version, graph.Offsets.Span);

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
            writer.AddRecords<int>(SectionType.GraphTypeIds, Version, typeIds.Span);
            byte[] namesBlob = EncodeNames(typeNames);
            writer.AddSection(SectionType.GraphTypeNames, Version, 0, namesBlob, (ulong)typeNames.Length);
        }
        writer.Save(path);
    }

    /// <summary>Loads a graph from <paramref name="path"/>, or null if the file is missing a core section
    /// (a format mismatch — the caller should rebuild). Type columns are optional. The columns are
    /// zero-copy views into the memory-mapped slab — the returned graph owns the mapping and frees it on
    /// dispose, so it never doubles peak memory the way materializing every column into an array does.</summary>
    public static HeapGraph? Load(string path)
    {
        ContainerReader container = ContainerReader.Open(path);
        try
        {
            IReadOnlyList<Section> edgeChunkSections = container.GetSections(SectionType.GraphEdgesChunk);
            if (!container.TryGetSection(SectionType.GraphAddresses, out Section addresses) ||
                !container.TryGetSection(SectionType.GraphSizes, out Section sizes) ||
                !container.TryGetSection(SectionType.GraphOffsets, out Section offsets) ||
                !container.TryGetSection(SectionType.GraphEdgeChunkMeta, out Section chunkMeta) ||
                edgeChunkSections.Count == 0)
            {
                container.Dispose();
                return null;
            }
            if (addresses.Version != Version)
            {
                container.Dispose();
                return null; // stale format — caller rebuilds from the dump
            }

            var chunks = new ReadOnlyMemory<int>[edgeChunkSections.Count];
            for (int i = 0; i < chunks.Length; i++) chunks[i] = edgeChunkSections[i].AsMemory<int>();
            var edges = new EdgeColumn(chunks, chunkMeta.AsRecords<long>().ToArray());

            ReadOnlyMemory<int>? typeIds = null;
            string[]? typeNames = null;
            if (container.TryGetSection(SectionType.GraphTypeIds, out Section typeIdSection) &&
                container.TryGetSection(SectionType.GraphTypeNames, out Section typeNameSection))
            {
                typeIds = typeIdSection.AsMemory<int>();
                typeNames = DecodeNames(typeNameSection.Data.Span);
            }

            ulong freeBytes = 0;
            long freeCount = 0;
            if (container.TryGetSection(SectionType.GraphMeta, out Section meta))
            {
                var m = meta.AsRecords<ulong>();
                if (m.Length >= 1) freeBytes = m[0];
                if (m.Length >= 2) freeCount = (long)m[1];
            }

            return new HeapGraph(
                addresses.AsMemory<ulong>(),
                sizes.AsMemory<uint>(),
                offsets.AsMemory<long>(),
                edges,
                typeIds,
                typeNames,
                freeBytes,
                freeCount,
                backing: container);
        }
        catch
        {
            container.Dispose();
            throw;
        }
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
