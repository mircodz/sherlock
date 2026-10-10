using Sherlock.Core;
using Sherlock.Core.Profiling;
using Spectre.Console;

namespace Sherlock.CLI.Rendering;

/// <summary>Escaped markup for values that appear in many commands, so each looks the same everywhere. These return
/// markup: use them in markup strings, tables, and trees, but never as holes in <c>MarkupLineInterpolated</c>, which
/// would escape them.</summary>
public static class Styled
{
    /// <summary>A type by its short name (see <see cref="TypeNames.Short"/>).</summary>
    public static string Type(string typeName) => $"[{Palette.Name}]{Markup.Escape(TypeNames.Short(typeName))}[/]";

    /// <summary>The namespace column of a per-type table, which keeps same-named types apart.</summary>
    public static string Namespace(string typeName) => Muted(TypeNames.Namespace(typeName));

    /// <summary>A profiler frame without its namespace (see <see cref="FrameNames.ShortMethod"/>); pair it with a
    /// <see cref="Namespace"/> column of the frame's declaring type.</summary>
    public static string Method(string frame) => $"[{Palette.Name}]{Markup.Escape(FrameNames.ShortMethod(frame))}[/]";

    public static string Address(ulong address) => $"[{Palette.Address}]{Addresses.Format(address)}[/]";

    /// <summary>The headline size of a row or summary.</summary>
    public static string HotSize(long bytes) => $"[bold {Palette.Hot}]{ByteSize.Format(bytes)}[/]";

    public static string Muted(string text) => $"[{Palette.Muted}]{Markup.Escape(text)}[/]";

    /// <summary>"Type 0x… · size": how one object is labelled everywhere.</summary>
    public static string Object(string typeName, ulong address, long size) =>
        $"{Type(typeName)} {Address(address)} [{Palette.Muted}]·[/] {ByteSize.Format(size)}";
}
