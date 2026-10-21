using System;

namespace Sherlock.Storage;

/// <summary>What a slab holds. Each kind has its own version, covering all its sections.</summary>
public enum SlabKind : ushort
{
    Profile = 1,
    Graph = 2,
    Dominators = 3,
    Inbound = 4,
}

/// <summary>
/// The container layout shared with the native profiler:
/// <code>
/// 0   "SHRK"
/// 4   u16 container version
/// 6   u16 kind
/// 8   u32 kind version
/// 12  u32 section count
/// 16  section table, 24 bytes each: u32 type, u32 record size, u64 offset, u64 length
///     sections, each starting on an <see cref="Alignment"/> boundary
/// </code>
/// Everything is little-endian.
/// </summary>
public static class ContainerFormat
{
    public static ReadOnlySpan<byte> Magic => "SHRK"u8;

    public const ushort Version = 2;
    public const int HeaderSize = 16;
    public const int SectionEntrySize = 24;
    public const int Alignment = 4096;
}
