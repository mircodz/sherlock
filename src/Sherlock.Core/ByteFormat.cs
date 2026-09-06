using System.Globalization;

namespace Sherlock.Core;

/// <summary>Culture-independent byte sizes shared by diagnostics, exports, and terminal views.</summary>
public static class ByteFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Human(long bytes) => Format(bytes, bytes.ToString(CultureInfo.InvariantCulture));
    public static string Human(ulong bytes) => Format(bytes, bytes.ToString(CultureInfo.InvariantCulture));

    private static string Format(double value, string bytes)
    {
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {Units[unit]}";
    }
}
