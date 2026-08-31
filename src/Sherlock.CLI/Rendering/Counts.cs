using System.Globalization;

namespace Sherlock.CLI.Rendering;

/// <summary>Formats large counts compactly (e.g. 1234 -> 1.2K, 3400000 -> 3.4M).</summary>
public static class Counts
{
    private static readonly string[] Suffixes = ["", "K", "M", "B"];

    public static string Compact(long n)
    {
        if (n < 1000)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        double value = n;
        int unit = 0;
        while (value >= 1000 && unit < Suffixes.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return $"{value.ToString("0.#", CultureInfo.InvariantCulture)}{Suffixes[unit]}";
    }

    public static string Format(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    public static string Percent(double value, int decimals = 1) => value.ToString(decimals == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + "%";
}
