namespace Sherlock.CLI.Rendering;

/// <summary>Formats large counts compactly (e.g. 1234 -> 1.2K, 3400000 -> 3.4M).</summary>
public static class Counts
{
    private static readonly string[] Suffixes = ["", "K", "M", "B"];

    public static string Compact(long n)
    {
        if (n < 1000)
        {
            return n.ToString();
        }

        double value = n;
        int unit = 0;
        while (value >= 1000 && unit < Suffixes.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return $"{value:0.#}{Suffixes[unit]}";
    }
}
