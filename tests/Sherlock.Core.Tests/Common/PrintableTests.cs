using Sherlock.Heap;
using Xunit;

namespace Sherlock.Core.Tests.Common;

public sealed class PrintableTests
{
    [Theory]
    [InlineData("List<T>.Add(T)", "List<T>.Add(T)")]
    [InlineData("Größe 名前 \U0001D49C", "Größe 名前 \U0001D49C")]
    [InlineData("tab\there", "tab\\there")]
    [InlineData("line\nbreak\r", "line\\nbreak\\r")]
    [InlineData("nul\0", "nul\\0")]
    [InlineData("\u001b[2J", "\\u001b[2J")]
    [InlineData("\u0085next", "\\u0085next")]
    [InlineData("right\u202eto left", "right\\u202eto left")]
    [InlineData("zero\u200bwidth", "zero\\u200bwidth")]
    [InlineData("line\u2028separator", "line\\u2028separator")]
    [InlineData("tag\U000E0041", "tag\\U000e0041")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("\\u0041", "\\\\u0041")]
    public void EscapesWhatCouldDriveOrDisguiseTheTerminal(string text, string expected)
    {
        Assert.Equal(expected, Printable.Escape(text));
    }

    [Fact]
    public void EscapesLoneSurrogatesButKeepsPairs()
    {
        Assert.Equal("a\\ud800b", Printable.Escape("a\ud800b"));
        Assert.Equal("a\\udc00", Printable.Escape("a\udc00"));
        Assert.Equal("\\ud800", Printable.Escape("\ud800"));
        Assert.Equal("\\udc00\\ud800", Printable.Escape("\udc00\ud800"));
        Assert.Equal("\U0001F600", Printable.Escape("\U0001F600"));
    }

    [Fact]
    public void EscapesTheQuoteTheTextIsShownIn()
    {
        Assert.Equal("\\\"quoted\\\" it's", Printable.Escape("\"quoted\" it's", quote: '"'));
        Assert.Equal("\\'", Printable.Escape("'", quote: '\''));
        Assert.Equal("\"", Printable.Escape("\""));
    }

    [Fact]
    public void ABoundedResultEndsWithAnEllipsisOnlyWhenCut()
    {
        string text = new('x', 256);
        Assert.Same(text, Printable.Escape(text, 256));
        Assert.Equal(new string('x', 255) + "…", Printable.Escape(new string('x', 100_000), 256));
    }

    [Fact]
    public void ABoundedResultNeverSplitsAnEscapeOrACharacter()
    {
        Assert.Equal(new string('x', 253) + "…", Printable.Escape(new string('x', 253) + "\u001bmore", 256));
        Assert.Equal(new string('x', 254) + "…", Printable.Escape(new string('x', 254) + "😀more", 256));
        Assert.Equal(new string('x', 254) + "😀", Printable.Escape(new string('x', 254) + "😀", 256));
    }

    [Fact]
    public void EscapedTextHasNoCharacterThatDrivesTheTerminal()
    {
        string escaped = Printable.Escape("first\nsecond\r\n\t\0\u001b[31mred\u0085\u2028\u2029\u202e\ud800\U000E0041");

        Assert.DoesNotContain(escaped, c => char.IsControl(c) || char.IsSurrogate(c) ||
            System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator);
    }

    [Fact]
    public void ReturnsPrintableTextUnchanged()
    {
        const string text = "Dictionary<TKey, TValue>+Entry[]";

        Assert.Same(text, Printable.Escape(text));
    }
}
