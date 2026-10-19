using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace Sherlock.Core.Storage;

/// <summary>
/// Owns a mapped slab container. Columns borrow the mapping and are valid until disposal;
/// blobs are copied into owned arrays. Sections and columns may exceed 2 GiB.
/// </summary>
public sealed class SlabFile : IDisposable
{
    private readonly record struct Section(uint RecordSize, long Offset, long Length);

    private readonly MappedFile _file;
    private readonly Dictionary<SectionType, Section> _sections = [];

    private SlabFile(MappedFile file, SlabKind kind, uint version)
    {
        _file = file;

        if (file.Length < ContainerFormat.HeaderSize)
        {
            throw new InvalidDataException("The file is too short to hold a Sherlock container.");
        }
        ReadOnlySpan<byte> head = file.Span(0, ContainerFormat.HeaderSize);
        if (!head[..4].SequenceEqual(ContainerFormat.Magic))
        {
            throw new InvalidDataException("The file isn't a Sherlock container.");
        }
        var actualKind = (SlabKind)BinaryPrimitives.ReadUInt16LittleEndian(head[6..]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(head[4..]) != ContainerFormat.Version ||
            (actualKind == kind && BinaryPrimitives.ReadUInt32LittleEndian(head[8..]) != version))
        {
            throw new InvalidDataException(kind == SlabKind.Profile
                ? "The profile was recorded by an incompatible version of Sherlock; record it again."
                : $"The file holds {Describe(kind)} written by an incompatible version of Sherlock.");
        }
        if (actualKind != kind)
        {
            throw new InvalidDataException($"The file holds {Describe(actualKind)}, not {Describe(kind)}.");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
        long tableEnd = ContainerFormat.HeaderSize + (long)count * ContainerFormat.SectionEntrySize;
        if (tableEnd > file.Length || tableEnd > int.MaxValue)
        {
            throw new InvalidDataException("The section table runs past the end of the file.");
        }
        ReadOnlySpan<byte> table = file.Span(ContainerFormat.HeaderSize, (int)(tableEnd - ContainerFormat.HeaderSize));
        var occupied = new List<(long Start, long End, SectionType Type)>((int)count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = table.Slice(i * ContainerFormat.SectionEntrySize, ContainerFormat.SectionEntrySize);
            var type = (SectionType)BinaryPrimitives.ReadUInt32LittleEndian(entry);
            uint recordSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
            if (recordSize == 0 || length % recordSize != 0)
            {
                throw new InvalidDataException($"Section {type} isn't a whole number of {recordSize}-byte records.");
            }
            if (offset > (ulong)file.Length || length > (ulong)file.Length - offset)
            {
                throw new InvalidDataException($"Section {type} runs past the end of the file.");
            }
            if (length > 0)
            {
                if (offset < (ulong)tableEnd)
                {
                    throw new InvalidDataException($"Section {type} overlaps the section table.");
                }
                if (offset % ContainerFormat.Alignment != 0)
                {
                    throw new InvalidDataException($"Section {type} isn't aligned to {ContainerFormat.Alignment} bytes.");
                }
                occupied.Add(((long)offset, (long)(offset + length), type));
            }
            if (!_sections.TryAdd(type, new Section(recordSize, (long)offset, (long)length)))
            {
                throw new InvalidDataException($"Section {type} appears twice.");
            }
        }

        occupied.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        for (int i = 1; i < occupied.Count; i++)
        {
            if (occupied[i].Start < occupied[i - 1].End)
            {
                throw new InvalidDataException($"Sections {occupied[i - 1].Type} and {occupied[i].Type} overlap.");
            }
        }
    }

    /// <summary>Maps the slab at <paramref name="path"/>, requiring it to hold <paramref name="kind"/> at
    /// <paramref name="version"/>.</summary>
    public static SlabFile Open(string path, SlabKind kind, uint version)
    {
        MappedFile file = MappedFile.Open(path);
        try
        {
            return new SlabFile(file, kind, version);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public bool Has(SectionType type) => _sections.ContainsKey(type);

    /// <summary>The section's records, without copying; empty when the section is absent.</summary>
    public Column<T> GetColumn<T>(SectionType type) where T : unmanaged
    {
        if (!_sections.TryGetValue(type, out Section section))
        {
            return Column<T>.Empty;
        }
        if (section.RecordSize != Unsafe.SizeOf<T>())
        {
            throw new InvalidDataException($"Section {type} holds {section.RecordSize}-byte records, not {typeof(T).Name}.");
        }
        return new Column<T>(_file, section.Offset, section.Length / section.RecordSize);
    }

    /// <summary>Copies a section's bytes into an owned array; empty when the section is absent.</summary>
    public byte[] Blob(SectionType type) =>
        _sections.TryGetValue(type, out Section section) ? _file.Span(section.Offset, checked((int)section.Length)).ToArray() : [];

    public void Dispose() => _file.Dispose();

    private static string Describe(SlabKind kind) => kind switch
    {
        SlabKind.Profile => "an allocation profile",
        SlabKind.Graph => "a heap graph",
        SlabKind.Dominators => "a dominator tree",
        SlabKind.Inbound => "a reference index",
        _ => $"an unknown kind of data ({(ushort)kind})",
    };
}
