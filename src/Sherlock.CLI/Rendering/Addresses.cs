using System;
using System.Globalization;

namespace Sherlock.CLI.Rendering;

/// <summary>Parses object addresses entered as hex, with or without a 0x prefix.</summary>
public static class Addresses
{
    public static bool TryParse(string text, out ulong address)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }
}
