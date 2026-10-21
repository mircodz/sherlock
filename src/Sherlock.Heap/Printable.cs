using System.Globalization;
using System.Text;

namespace Sherlock.Heap;

/// <summary>Names and text read from a dump or a profile come from the process under analysis. Before they are
/// displayed, characters that could drive or disguise the terminal (controls, bidi and other format characters,
/// line separators, lone surrogates) are spelled as escapes, and a backslash is doubled so escapes can't be faked.
/// This is the one place that decides what is safe to show; names stay raw everywhere else, so they still match.</summary>
public static class Printable
{
    /// <param name="maxLength">The longest result; a longer one is cut before an escape or character it would split,
    /// and ends with "…".</param>
    /// <param name="quote">A quote character to escape too, for text shown inside that quote.</param>
    public static string Escape(string text, int maxLength = int.MaxValue, char? quote = null)
    {
        int first = 0;
        while (first < text.Length && !NeedsEscape(text, first, quote))
        {
            first += Width(text, first);
        }
        if (first == text.Length && text.Length <= maxLength)
        {
            return text;
        }

        var result = new StringBuilder(System.Math.Min(text.Length + 16, maxLength));
        for (int i = 0; i < text.Length;)
        {
            int width = Width(text, i);
            string token = NeedsEscape(text, i, quote) ? EscapeAt(text, i, width, quote) : text.Substring(i, width);
            // Keep room for the ellipsis unless this is the last token.
            int limit = i + width < text.Length ? maxLength - 1 : maxLength;
            if (result.Length + token.Length > limit)
            {
                result.Append('…');
                break;
            }
            result.Append(token);
            i += width;
        }
        return result.ToString();
    }

    private static string EscapeAt(string text, int index, int width, char? quote) => text[index] switch
    {
        '\\' => "\\\\",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        '\0' => "\\0",
        _ when width == 2 => $"\\U{char.ConvertToUtf32(text[index], text[index + 1]):x8}",
        var c when c == quote => "\\" + c,
        var c => $"\\u{(int)c:x4}",
    };

    // A surrogate pair is one character; a lone surrogate is one unit.
    private static int Width(string text, int index) =>
        char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;

    private static bool NeedsEscape(string text, int index, char? quote)
    {
        if (!Rune.TryGetRuneAt(text, index, out Rune rune))
        {
            return true; // a lone surrogate
        }
        return rune.Value == '\\' || rune.Value == quote || Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune)
            is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
    }
}
