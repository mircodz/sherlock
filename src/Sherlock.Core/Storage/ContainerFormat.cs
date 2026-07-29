using System;

namespace Sherlock.Core.Storage;

public static class ContainerFormat
{
    /// <summary>File magic: ASCII <c>SHRK</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "SHRK"u8;

    public const ushort FormatVersion = 1;
    public const ushort FlagLittleEndian = 0x1;

    public const int HeaderSize = 16;
    public const int SectionEntrySize = 32;
    public const int Alignment = 8;

    /// <summary>Default max bytes per chunk when splitting a large fixed-width column
    /// (<see cref="ContainerWriter.AddChunkedRecords{T}"/>). Well under the reader's ~2&nbsp;GB
    /// single-section cap; a multiple of 8 so no 8/4/2-byte record straddles a chunk. Mirrors the
    /// native <c>kDefaultChunkBytes</c>.</summary>
    public const long DefaultChunkBytes = 256L << 20; // 256 MiB
}
