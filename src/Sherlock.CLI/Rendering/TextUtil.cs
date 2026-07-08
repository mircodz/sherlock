namespace Sherlock.CLI.Rendering;

/// <summary>Small text helpers for command output.</summary>
public static class TextUtil
{
    /// <summary>
    /// A single-line preview of a value: newlines collapsed to spaces, truncated to
    /// <paramref name="max"/> characters with an ellipsis. Does not escape markup — the
    /// caller escapes if it renders through Spectre markup.
    /// </summary>
    public static string Preview(string value, int max = 64)
    {
        value = value.ReplaceLineEndings(" ");
        return value.Length > max ? value[..(max - 1)] + "…" : value;
    }
}
