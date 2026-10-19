using System;
using System.IO;
using System.Text;
using Sherlock.Core.Profiling;
using Sherlock.Core.Storage;

namespace Sherlock.Core.HeapModel;

/// <summary>Persists a heap graph as typed slab columns.</summary>
public static class HeapGraphStore
{
    internal const uint Version = 3;

    private static readonly SectionType[] Required =
    [
        SectionType.GraphMeta, SectionType.GraphAddresses, SectionType.GraphSizes, SectionType.GraphTypeIds, SectionType.Strings,
        SectionType.Types, SectionType.GraphOffsets, SectionType.GraphEdges, SectionType.GraphRoots,
    ];

    public static void Save(string path, HeapGraph graph, string? sourcePath = null)
    {
        var writer = new ContainerWriter(SlabKind.Graph, Version);
        (long sourceLength, long sourceModified) = SourceStamp(sourcePath);
        writer.AddRecords(SectionType.GraphMeta, [new GraphMeta(graph.Id, graph.FreeBytes, graph.FreeCount, sourceLength, sourceModified)]);
        writer.AddMemory(SectionType.GraphAddresses, graph.Addresses);
        writer.AddMemory(SectionType.GraphSizes, graph.Sizes);
        writer.AddMemory(SectionType.GraphLargeSizes, graph.LargeSizes);
        writer.AddMemory(SectionType.GraphTypeIds, graph.TypeIds);
        (byte[] strings, TypeRecord[] types) = EncodeNames(graph.TypeNames);
        writer.AddBytes(SectionType.Strings, strings);
        writer.AddRecords<TypeRecord>(SectionType.Types, types);
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
            foreach (SectionType section in Required)
            {
                if (!slab.Has(section))
                {
                    throw new InvalidDataException($"The heap graph has no {section} section.");
                }
            }
            Column<GraphMeta> metaColumn = slab.GetColumn<GraphMeta>(SectionType.GraphMeta);
            if (metaColumn.Length != 1)
            {
                throw new InvalidDataException("The heap graph's metadata is malformed.");
            }
            GraphMeta meta = metaColumn[0];
            if (sourcePath is not null)
            {
                (long sourceLength, long sourceModified) = SourceStamp(sourcePath);
                if (meta.SourceLength != sourceLength || meta.SourceModified != sourceModified)
                {
                    slab.Dispose();
                    return null;
                }
            }

            return new HeapGraph(
                meta.Id,
                slab.GetColumn<ulong>(SectionType.GraphAddresses).AsMemory(),
                slab.GetColumn<uint>(SectionType.GraphSizes).AsMemory(),
                slab.GetColumn<LargeSize>(SectionType.GraphLargeSizes).AsMemory(),
                slab.GetColumn<long>(SectionType.GraphOffsets).AsMemory(),
                new EdgeColumn(slab.GetColumn<int>(SectionType.GraphEdges)),
                slab.GetColumn<int>(SectionType.GraphTypeIds).AsMemory(),
                DecodeNames(slab.Blob(SectionType.Strings), slab.GetColumn<TypeRecord>(SectionType.Types)),
                meta.FreeBytes,
                meta.FreeCount,
                slab.GetColumn<HeapRootRecord>(SectionType.GraphRoots).AsMemory(),
                slab);
        }
        catch
        {
            slab.Dispose();
            throw;
        }
    }

    internal static (long Length, long Modified) SourceStamp(string? path)
    {
        if (path is null)
        {
            return (0, 0);
        }
        var file = new FileInfo(path);
        return (file.Length, file.LastWriteTimeUtc.Ticks);
    }

    private static (byte[] Strings, TypeRecord[] Types) EncodeNames(string[] names)
    {
        var types = new TypeRecord[names.Length];
        var strings = new MemoryStream();
        for (int i = 0; i < names.Length; i++)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(names[i]);
            types[i] = new TypeRecord { Name = new StringRef { Offset = checked((uint)strings.Length), Length = (uint)utf8.Length } };
            strings.Write(utf8);
        }
        return (strings.ToArray(), types);
    }

    private static string[] DecodeNames(byte[] strings, Column<TypeRecord> types)
    {
        var names = new string[types.Length];
        for (int i = 0; i < names.Length; i++)
        {
            StringRef name = types[i].Name;
            if ((ulong)name.Offset + name.Length > (ulong)strings.Length)
            {
                throw new InvalidDataException("A type name runs past the string table.");
            }
            names[i] = Encoding.UTF8.GetString(strings, (int)name.Offset, (int)name.Length);
        }
        return names;
    }
}
