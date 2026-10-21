using Sherlock.Core;
using Sherlock.Core.Profiling;
using Sherlock.Heap;
using Spectre.Console;

namespace Sherlock.CLI.Rendering;

/// <summary>Escaped markup for values that appear in many commands, so each looks the same everywhere. These return
/// markup: use them in markup strings, tables, and trees, but never as holes in <c>MarkupLineInterpolated</c>, which
/// would escape them. Helpers that take raw text from the dump make it printable first (see <see cref="Printable"/>);
/// in interpolated markup, pass such text through <see cref="Printable.Escape"/> yourself.</summary>
public static class Styled
{
    /// <summary>Raw text from the dump (a field name, a message): printable and markup-escaped.</summary>
    public static string Text(string text) => Markup.Escape(Printable.Escape(text));

    /// <summary>A type by its short name (see <see cref="DisplayNames.Type"/>).</summary>
    public static string Type(string typeName) => $"[{Palette.Name}]{Markup.Escape(DisplayNames.Type(typeName))}[/]";

    /// <summary>The namespace column of a per-type table, which keeps same-named types apart.</summary>
    public static string Namespace(string typeName) => Muted(DisplayNames.Namespace(typeName));

    /// <summary>The namespace column of a per-method table: the declaring type's namespace.</summary>
    public static string Namespace(MethodFrame method) => Muted(Printable.Escape(method.Namespace));

    /// <summary>A method by its label (see <see cref="MethodFrame.Label"/>); pair it with a <see cref="Namespace(MethodFrame)"/> column.</summary>
    public static string Method(MethodFrame method) => $"[{Palette.Name}]{Markup.Escape(method.Label)}[/]";

    public static string Address(ulong address) => $"[{Palette.Address}]{Addresses.Format(address)}[/]";

    /// <summary>The headline size of a row or summary.</summary>
    public static string HotSize(long bytes) => $"[bold {Palette.Hot}]{ByteSize.Format(bytes)}[/]";

    /// <summary>Printable text in the muted color; for raw dump text use <see cref="Text"/> inside.</summary>
    public static string Muted(string text) => $"[{Palette.Muted}]{Markup.Escape(text)}[/]";

    /// <summary>"Type 0x… · size": how one object is labelled everywhere.</summary>
    public static string Object(string typeName, ulong address, long size) =>
        $"{Type(typeName)} {Address(address)} [{Palette.Muted}]·[/] {ByteSize.Format(size)}";
}
