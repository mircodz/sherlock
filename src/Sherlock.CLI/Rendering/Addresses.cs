using System;
using System.Globalization;

namespace Sherlock.CLI.Rendering;

/// <summary>Formats and parses object addresses: always lowercase hex with a 0x prefix and no padding.</summary>
public static class Addresses
{
    public static string Format(ulong address) => "0x" + address.ToString("x", CultureInfo.InvariantCulture);

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
