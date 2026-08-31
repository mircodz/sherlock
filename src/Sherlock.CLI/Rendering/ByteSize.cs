using System.Globalization;

namespace Sherlock.CLI.Rendering;

/// <summary>Formats byte counts as human-readable sizes (e.g. 1.5 MB).</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "n/a";
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes.ToString(CultureInfo.InvariantCulture)} {Units[unit]}"
            : $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {Units[unit]}";
    }
}
