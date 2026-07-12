namespace Sherlock.Core;

/// <summary>Human byte sizes for Core-side output (export writers, diagnostics).</summary>
public static class ByteFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Human(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {Units[unit]}";
    }

    public static string Human(ulong bytes) => Human((long)bytes);
}
