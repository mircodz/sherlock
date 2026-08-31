using Spectre.Console;

namespace Sherlock.CLI.Rendering;

/// <summary>The shared semantic palette for the REPL and TUI.</summary>
public static class Theme
{
    public const string Text = "#F2F2F2";
    public const string Muted = "#808791";
    public const string Section = "#3A8DFF";
    public const string Focus = "#5AF78E";
    public const string Magenta = "#FF2E88";
    public const string Identity = "#00D7FF";
    public const string Address = "#FFD75F";
    public const string String = "#F2F2F2";
    public const string Success = "#AFFF00";
    public const string Hot = "#AFFF00";
    public const string Attention = "#FFAF00";
    public const string Error = "#FF3B5C";

    public static Color MutedColor { get; } = new(0x80, 0x87, 0x91);
    public static Color SectionColor { get; } = new(0x3A, 0x8D, 0xFF);
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
            Foreground = Cellar.Primitives.Color.Hex(Text),
            Background = Cellar.Primitives.Color.Hex("#080A0D"),
            Accent = Cellar.Primitives.Color.Hex(Section),
            Secondary = Cellar.Primitives.Color.Hex(Address),
            Muted = Cellar.Primitives.Color.Hex(Muted),
            Border = Cellar.Primitives.Color.Hex("#24566B"),
            SelectionForeground = Cellar.Primitives.Color.Hex("#080A0D"),
            SelectionBackground = Cellar.Primitives.Color.Hex(Hot),
            Success = Cellar.Primitives.Color.Hex(Success),
            Warning = Cellar.Primitives.Color.Hex(Attention),
            Error = Cellar.Primitives.Color.Hex(Error),
            Info = Cellar.Primitives.Color.Hex(Section),
        };
    }

    public static Cellar.Primitives.Color[] ChartColors() =>
    [
        Cellar.Primitives.Color.Hex(Identity),
        Cellar.Primitives.Color.Hex(Hot),
        Cellar.Primitives.Color.Hex(Address),
        Cellar.Primitives.Color.Hex(Magenta),
        Cellar.Primitives.Color.Hex(Attention),
        Cellar.Primitives.Color.Hex(Section),
    ];
}
