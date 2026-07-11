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
}
