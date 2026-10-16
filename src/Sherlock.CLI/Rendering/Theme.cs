using Spectre.Console;

namespace Sherlock.CLI.Rendering;

/// <summary>The one semantic palette for the REPL and TUI. Spectre markup escapes interpolated values only by
/// doubling brackets, so these hex values are safe in <c>$"[{Palette.Muted}]…[/]"</c>, interpolated or not.</summary>
public static class Palette
{
    /// <summary>Primary text and plain values.</summary>
    public const string Text = "#F2F2F2";
    /// <summary>Secondary text: units, separators, hints.</summary>
    public const string Muted = "#808791";
    /// <summary>Section headings.</summary>
    public const string Heading = "#3A8DFF";
    /// <summary>Type, method, and other identifier names.</summary>
    public const string Name = "#00D7FF";
    public const string Address = "#FFD75F";
    /// <summary>The headline metric (retained/allocated bytes), the brand, and success.</summary>
    public const string Hot = "#AFFF00";
    public const string Warning = "#FFAF00";
    public const string Error = "#FF3B5C";
    public const string Magenta = "#FF2E88";
    public const string Background = "#080A0D";
    public const string Border = "#24566B";
}

/// <summary>Spectre/Cellar helpers over <see cref="Palette"/>.</summary>
public static class Theme
{
    public static Color MutedColor { get; } = Color.FromHex(Palette.Muted);
    public static Color SectionColor { get; } = Color.FromHex(Palette.Heading);
    /// <summary>The shared borderless result table.</summary>
    public static Table Table(bool expand = false)
    {
        var table = new Table().Border(TableBorder.None);
        return expand ? table.Expand() : table;
    }

    public static void ApplyCellar()
    {
        Cellar.Theming.Theme.Current = new Cellar.Theming.Theme
        {
            Name = "Sherlock",
            Foreground = Cellar.Primitives.Color.Hex(Palette.Text),
            Background = Cellar.Primitives.Color.Hex(Palette.Background),
            // Accent marks names (types, methods, links) as in the REPL; Info marks headings.
            Accent = Cellar.Primitives.Color.Hex(Palette.Name),
            Secondary = Cellar.Primitives.Color.Hex(Palette.Address),
            Muted = Cellar.Primitives.Color.Hex(Palette.Muted),
            Border = Cellar.Primitives.Color.Hex(Palette.Border),
            SelectionForeground = Cellar.Primitives.Color.Hex(Palette.Background),
            SelectionBackground = Cellar.Primitives.Color.Hex(Palette.Hot),
            Success = Cellar.Primitives.Color.Hex(Palette.Hot),
            Warning = Cellar.Primitives.Color.Hex(Palette.Warning),
            Error = Cellar.Primitives.Color.Hex(Palette.Error),
            Info = Cellar.Primitives.Color.Hex(Palette.Heading),
        };
    }

    public static Cellar.Primitives.Color[] ChartColors() =>
    [
        Cellar.Primitives.Color.Hex(Palette.Name),
        Cellar.Primitives.Color.Hex(Palette.Hot),
        Cellar.Primitives.Color.Hex(Palette.Address),
        Cellar.Primitives.Color.Hex(Palette.Magenta),
        Cellar.Primitives.Color.Hex(Palette.Warning),
        Cellar.Primitives.Color.Hex(Palette.Heading),
    ];
}
