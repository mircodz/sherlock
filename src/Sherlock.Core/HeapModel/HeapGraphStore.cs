using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Sherlock.Core.Storage;

namespace Sherlock.Core.HeapModel;

/// <summary>Persists a heap graph as typed slab columns.</summary>
public static class HeapGraphStore
{
    private const ushort Version = 6;

    public static void Save(string path, HeapGraph graph, string? sourcePath = null)
    {
        var writer = new ContainerWriter();
        writer.AddChunkedMemoryRecords(SectionType.GraphAddresses, Version, graph.Addresses);
        writer.AddChunkedMemoryRecords(SectionType.GraphSizes, Version, graph.Sizes);
        writer.AddChunkedMemoryRecords(SectionType.GraphOffsets, Version, graph.Offsets);

        EdgeColumn edges = graph.Edges;
        IReadOnlyList<ReadOnlyMemory<byte>> edgeChunks = edges.ChunksAsBytes();
        for (int i = 0; i < edgeChunks.Count; i++)
        {
            writer.AddChunkedSection(SectionType.GraphEdgesChunk, Version, sizeof(int), [edgeChunks[i]],
                (ulong)(edges.ChunkStarts[i + 1] - edges.ChunkStarts[i]));
        }
        writer.AddRecords(SectionType.GraphEdgeChunkMeta, Version, edges.ChunkStarts);
        writer.AddMemoryRecords(SectionType.GraphRoots, Version, graph.Roots);

        (long sourceLength, long sourceModified) = SourceStamp(sourcePath);
        writer.AddRecords(SectionType.GraphMeta, Version, [graph.FreeBytes, (ulong)graph.FreeCount, (ulong)sourceLength, (ulong)sourceModified]);
        if (graph.TypeIds is { } typeIds && graph.TypeNames is { } typeNames)
        {
            writer.AddChunkedMemoryRecords(SectionType.GraphTypeIds, Version, typeIds);
            byte[] namesBlob = EncodeNames(typeNames);
            writer.AddSection(SectionType.GraphTypeNames, Version, 0, namesBlob, (ulong)typeNames.Length);
        }
        writer.Save(path);
    }

    /// <summary>Loads a compatible graph, keeping edge chunks memory-mapped.</summary>
    public static HeapGraph? Load(string path, string? sourcePath = null)
    {
        SlabFile slab = SlabFile.Open(path);
        try
        {
            if (!slab.Has(SectionType.GraphOffsets) || !slab.Has(SectionType.GraphEdgeChunkMeta) || !slab.Has(SectionType.GraphRoots))
            {
                slab.Dispose();
                return null;
            }
            if (slab.SectionVersion(SectionType.GraphOffsets) != Version || slab.SectionVersion(SectionType.GraphMeta) != Version ||
                slab.SectionVersion(SectionType.GraphRoots) != Version)
            {
                slab.Dispose();
                return null;
            }

            Column<ulong> meta = slab.GetColumn<ulong>(SectionType.GraphMeta);
            if (sourcePath is not null)
            {
                (long sourceLength, long sourceModified) = SourceStamp(sourcePath);
                if (meta.Length < 4 || meta[2] != (ulong)sourceLength || meta[3] != (ulong)sourceModified)
                {
                    slab.Dispose();
                    return null;
                }
            }

            ReadOnlyMemory<ulong> addresses = Materialize(slab.GetColumn<ulong>(SectionType.GraphAddresses));
            ReadOnlyMemory<uint> sizes = Materialize(slab.GetColumn<uint>(SectionType.GraphSizes));
            ReadOnlyMemory<long> offsets = Materialize(slab.GetColumn<long>(SectionType.GraphOffsets));
            ReadOnlyMemory<HeapRootRecord> roots = Materialize(slab.GetColumn<HeapRootRecord>(SectionType.GraphRoots));

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
            if (meta.Length >= 1)
            {
                freeBytes = meta[0];
            }

            if (meta.Length >= 2)
            {
                freeCount = (long)meta[1];
            }

            return new HeapGraph(addresses, sizes, offsets, edges, typeIds, typeNames, freeBytes, freeCount, roots, slab);
        }
        catch
        {
            slab.Dispose();
            throw;
        }
    }

    private static ReadOnlyMemory<T> Materialize<T>(Column<T> col) where T : unmanaged =>
        MaterializeArray(col);

    private static (long Length, long Modified) SourceStamp(string? path)
    {
        if (path is null)
        {
            return (0, 0);
        }
        var file = new FileInfo(path);
        return (file.Length, file.LastWriteTimeUtc.Ticks);
    }

    private static T[] MaterializeArray<T>(Column<T> col) where T : unmanaged
    {
        var arr = GC.AllocateUninitializedArray<T>(checked((int)col.Length));
        col.CopyTo(0, arr);
        return arr;
    }

    // [u32 count][ (u32 byteLen, utf8 bytes) x count ], little-endian.
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
