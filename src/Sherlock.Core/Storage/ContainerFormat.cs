using System;

namespace Sherlock.Core.Storage;

public static class ContainerFormat
{
    public static ReadOnlySpan<byte> Magic => "SHRK"u8;

    public const ushort FormatVersion = 1;
    public const ushort FlagLittleEndian = 0x1;

    public const int HeaderSize = 16;
    public const int SectionEntrySize = 32;
    public const int Alignment = 8;

    /// <summary>Default chunk size for fixed-width columns. A multiple of 8, matching native
    /// <c>kDefaultChunkBytes</c> and keeping each chunk within byte-span limits.</summary>
    public const long DefaultChunkBytes = 256L << 20; // 256 MiB
}
