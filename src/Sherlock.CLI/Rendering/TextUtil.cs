namespace Sherlock.CLI.Rendering;

/// <summary>Small text helpers for command output.</summary>
public static class TextUtil
{
    /// <summary>Single-line preview capped at <paramref name="max"/> characters. Callers must escape markup.</summary>
    public static string Preview(string value, int max = 64)
    {
        value = value.ReplaceLineEndings(" ");
        return value.Length > max ? value[..(max - 1)] + "…" : value;
    }
}
