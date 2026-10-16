using System.Globalization;

namespace Sherlock.Core;

/// <summary>Culture-independent byte sizes shared by diagnostics, exports, and terminal views.</summary>
public static class ByteFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Human(long bytes) => Format(bytes, bytes.ToString(CultureInfo.InvariantCulture), "0.##");
    public static string Human(ulong bytes) => Format(bytes, bytes.ToString(CultureInfo.InvariantCulture), "0.##");

    /// <summary>For table columns: always two decimals, so sizes line up ("1.00 KB" above "45.29 KB").</summary>
    public static string Column(long bytes) => Format(bytes, bytes.ToString(CultureInfo.InvariantCulture), "0.00");
    public static string Column(ulong bytes) => Format(bytes, bytes.ToString(CultureInfo.InvariantCulture), "0.00");

    private static string Format(double value, string bytes, string decimals)
    {
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value.ToString(decimals, CultureInfo.InvariantCulture)} {Units[unit]}";
    }
}
