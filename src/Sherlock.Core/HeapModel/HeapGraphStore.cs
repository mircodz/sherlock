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
    private const uint Version = 2;

    public static void Save(string path, HeapGraph graph, string? sourcePath = null)
    {
        var writer = new ContainerWriter(SlabKind.Graph, Version);
        (long sourceLength, long sourceModified) = SourceStamp(sourcePath);
        writer.AddRecords<ulong>(SectionType.GraphMeta, [graph.FreeBytes, (ulong)graph.FreeCount, (ulong)sourceLength, (ulong)sourceModified]);
        writer.AddMemory(SectionType.GraphAddresses, graph.Addresses);
        writer.AddMemory(SectionType.GraphSizes, graph.Sizes);
        if (graph.TypeIds is { } typeIds && graph.TypeNames is { } typeNames)
        {
            writer.AddMemory(SectionType.GraphTypeIds, typeIds);
            writer.AddBytes(SectionType.GraphTypeNames, EncodeNames(typeNames));
        }
        writer.AddMemory(SectionType.GraphOffsets, graph.Offsets);
        writer.AddMemory(SectionType.GraphEdges, graph.Edges.Parts());
        writer.AddMemory(SectionType.GraphRoots, graph.Roots);
        writer.Save(path);
    }

    /// <summary>Loads a graph that matches <paramref name="sourcePath"/>, or null when it doesn't. Columns stay in
    /// the mapping, which the graph owns.</summary>
    public static HeapGraph? Load(string path, string? sourcePath = null)
    {
        SlabFile slab = SlabFile.Open(path, SlabKind.Graph, Version);
        try
        {
            Column<ulong> meta = slab.GetColumn<ulong>(SectionType.GraphMeta);
            if (meta.Length != 4)
            {
                throw new InvalidDataException("The heap graph has no metadata.");
            }
            if (sourcePath is not null)
            {
                (long sourceLength, long sourceModified) = SourceStamp(sourcePath);
                if (meta[2] != (ulong)sourceLength || meta[3] != (ulong)sourceModified)
                {
                    slab.Dispose();
                    return null;
                }
            }

            ReadOnlyMemory<int>? typeIds = null;
            string[]? typeNames = null;
            if (slab.Has(SectionType.GraphTypeIds))
            {
                typeIds = slab.GetColumn<int>(SectionType.GraphTypeIds).AsMemory();
                typeNames = DecodeNames(slab.Blob(SectionType.GraphTypeNames));
            }
            return new HeapGraph(
                slab.GetColumn<ulong>(SectionType.GraphAddresses).AsMemory(),
                slab.GetColumn<uint>(SectionType.GraphSizes).AsMemory(),
                slab.GetColumn<long>(SectionType.GraphOffsets).AsMemory(),
                new EdgeColumn(slab.GetColumn<int>(SectionType.GraphEdges)),
                typeIds, typeNames, meta[0], (long)meta[1],
                slab.GetColumn<HeapRootRecord>(SectionType.GraphRoots).AsMemory(),
                slab);
        }
        catch
        {
            slab.Dispose();
            throw;
        }
    }

    private static (long Length, long Modified) SourceStamp(string? path)
    {
        if (path is null)
        {
            return (0, 0);
        }
        var file = new FileInfo(path);
        return (file.Length, file.LastWriteTimeUtc.Ticks);
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
