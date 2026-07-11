using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sherlock.Core.Storage;

/// <summary>Builds a storage container, byte-for-byte identical to the native writer (guarded by the golden-bytes test). Used for tests/tooling.</summary>
public sealed class ContainerWriter
{
    private readonly List<(SectionType Type, ushort Version, ushort RecordSize, ulong Count, byte[] Data)> _sections = [];

    /// <summary>Adds a section of raw bytes (<paramref name="recordSize"/> = 0 for blob sections).</summary>
    public void AddSection(SectionType type, ushort version, ushort recordSize, ReadOnlySpan<byte> data, ulong count)
        => _sections.Add((type, version, recordSize, count, data.ToArray()));

    /// <summary>Adds a fixed-width record section from a span of unmanaged <typeparamref name="T"/>.</summary>
    public void AddRecords<T>(SectionType type, ushort version, ReadOnlySpan<T> records) where T : struct
        => AddSection(type, version, (ushort)Unsafe.SizeOf<T>(), MemoryMarshal.AsBytes(records), (ulong)records.Length);

    public byte[] ToArray()
    {
        int tableEnd = ContainerFormat.HeaderSize + _sections.Count * ContainerFormat.SectionEntrySize;
        var offsets = new long[_sections.Count];
        long cursor = tableEnd;
        for (int i = 0; i < _sections.Count; i++)
        {
            cursor = Align(cursor);
            offsets[i] = cursor;
            cursor += _sections[i].Data.Length;
        }
        long total = _sections.Count == 0 ? tableEnd : cursor;

        var buf = new byte[total];
        Span<byte> s = buf;

        // Header.
        ContainerFormat.Magic.CopyTo(s);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], ContainerFormat.FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], ContainerFormat.FlagLittleEndian);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)_sections.Count);
        // reserved [12..16) stays zero

        // Section table.
        for (int i = 0; i < _sections.Count; i++)
        {
            Span<byte> e = s.Slice(ContainerFormat.HeaderSize + i * ContainerFormat.SectionEntrySize, ContainerFormat.SectionEntrySize);
            (SectionType type, ushort version, ushort recordSize, ulong count, byte[] data) = _sections[i];
            BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)type);
            BinaryPrimitives.WriteUInt16LittleEndian(e[4..], version);
            BinaryPrimitives.WriteUInt16LittleEndian(e[6..], recordSize);
            BinaryPrimitives.WriteUInt64LittleEndian(e[8..], (ulong)offsets[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(e[16..], (ulong)data.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(e[24..], count);
        }

        // Section data at its aligned offset.
        for (int i = 0; i < _sections.Count; i++)
        {
            _sections[i].Data.CopyTo(buf, (int)offsets[i]);
        }
        return buf;
    }

    private static long Align(long n) => (n + ContainerFormat.Alignment - 1) & ~((long)ContainerFormat.Alignment - 1);
}
