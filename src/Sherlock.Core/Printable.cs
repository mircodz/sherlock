using System.Globalization;
using System.Text;

namespace Sherlock.Core;

/// <summary>Names and text read from a dump or a profile come from the process under analysis. Before they are
/// displayed, characters that could drive or disguise the terminal (controls, bidi and other format characters,
/// line separators, lone surrogates) are spelled as escapes, and a backslash is doubled so escapes can't be faked.</summary>
public static class Printable
{
    public static string Escape(string text)
    {
        int first = 0;
        while (first < text.Length && !NeedsEscape(text, first))
        {
            first += Width(text, first);
        }
        if (first == text.Length)
        {
            return text;
        }

        var result = new StringBuilder(text.Length + 16).Append(text, 0, first);
        for (int i = first; i < text.Length; i += Width(text, i))
        {
            char c = text[i];
            if (!NeedsEscape(text, i))
            {
                result.Append(text, i, Width(text, i));
                continue;
            }
            result.Append(c switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\0' => "\\0",
                _ when Width(text, i) == 2 => $"\\U{char.ConvertToUtf32(c, text[i + 1]):x8}",
                _ => $"\\u{(int)c:x4}",
            });
        }
        return result.ToString();
    }

    // A surrogate pair is one character; a lone surrogate is one unit.
    private static int Width(string text, int index) =>
        char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;

    private static bool NeedsEscape(string text, int index)
    {
        if (!Rune.TryGetRuneAt(text, index, out Rune rune))
        {
            return true; // a lone surrogate
        }
        return rune.Value == '\\' || Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune)
            is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
    }
}
