using Spectre.Console;

namespace Sherlock.CLI.Rendering;

/// <summary>
/// The REPL's shared visual language, so every view reads as one tool. Accents are semantic:
/// grey = secondary/metadata, aqua = identifiers (ids, types, processes), green = sizes,
/// yellow = events/warnings, red = errors, gold1 = string values.
/// </summary>
public static class Theme
{
    /// <summary>The house table: a soft rounded border in dim grey, no inter-row rules.</summary>
    public static Table Table(bool expand = false)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        return expand ? table.Expand() : table;
    }
}
