using System;
using System.Globalization;
using System.Linq;
using Cellar.Terminal;
using Cellar.Widgets;
using Sherlock.CLI.Rendering;
using Sherlock.CLI.Tui;
using Sherlock.Core;
using Sherlock.Core.Profiling;
using Sherlock.Heap;
using Xunit;

namespace Sherlock.CLI.Tests;

public sealed class PresentationTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(1125899906842624, "1 PB")]
    public void ByteFormattingIsShared(long bytes, string expected)
    {
        Assert.Equal(expected, ByteFormat.Human(bytes));
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    [Theory]
    [InlineData(880, "880 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(9830, "9.60 KB")]
    [InlineData(46377, "45.29 KB")]
    public void ColumnSizesKeepTwoDecimals(long bytes, string expected)
    {
        Assert.Equal(expected, ByteFormat.Column(bytes));
    }

    [Fact]
    public void SizeColumnsSortByBytesNotByText()
    {
        Table<Row> table = ViewFormatting.Table([new Row("small", 900, 1), new Row("large", 20_000, 2), new Row("medium", 9_000, 3)], _ => { },
            ViewFormatting.BytesColumn<Row>("bytes", row => row.Bytes, Cellar.Primitives.Color.Default));

        table.SortBy(0, SortState.Descending); // as text, "900 B" > "8.79 KB" > "19.53 KB"

        Assert.Equal(["large", "medium", "small"], table.Rows.Select(row => row.Name));
    }

    [Fact]
    public void ByteFormattingIsCultureIndependent()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("1.5 KB", ByteFormat.Human(1536));
            Assert.Equal("1.5 KB", ByteSize.Format(1536));
            Assert.Equal("1.50 KB", ByteFormat.Column(1536));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void UnknownSizesKeepTheirExistingCliLabel()
    {
        Assert.Equal("n/a", ByteSize.Format(-1));
        Assert.Equal("-1 B", ByteFormat.Human(-1));
        Assert.Equal("16384 PB", ByteFormat.Human(ulong.MaxValue));
    }

    [Theory]
    [InlineData("System.Collections.Generic.List<App.Order>", "List<Order>")]
    [InlineData("System.Collections.Generic.Dictionary<System.String, System.Object>+Entry[]", "Dictionary<String, Object>+Entry[]")]
    [InlineData("System.Collections.Generic.Dictionary<System.String, System.Collections.Generic.List<App.Order[]>>", "Dictionary<String, List<Order[]>>")]
    [InlineData("System.Threading.PortableThreadPool+HillClimbing+LogEntry[]", "PortableThreadPool+HillClimbing+LogEntry[]")]
    [InlineData("System.String[,]", "String[,]")]
    [InlineData("App.Program+<>c", "Program+<>c")]
    [InlineData("App.Program+<Main>d__0", "Program+<Main>d__0")]
    [InlineData("<PrivateImplementationDetails>", "<PrivateImplementationDetails>")]
    [InlineData("App.Cache<T>+<>c__DisplayClass1_0<U>", "Cache<T>+<>c__DisplayClass1_0<U>")]
    [InlineData("Free", "Free")]
    [InlineData("", "")]
    public void ShortNamesDropNamespacesEverywhere(string name, string expected)
    {
        Assert.Equal(expected, TypeNames.Short(name));
    }

    [Fact]
    public void StyledValuesEscapeTypeNamesAndShareOneLabel()
    {
        Assert.Equal($"[{Palette.Name}]Byte[[]][/]", Styled.Type("System.Byte[]"));
        Assert.Equal($"[{Palette.Address}]0x1a[/]", Styled.Address(0x1a));
        Assert.Equal($"[{Palette.Name}]List<Order>[/] [{Palette.Address}]0x10[/] [{Palette.Muted}]·[/] 24 B",
            Styled.Object("System.Collections.Generic.List<App.Order>", 0x10, 24));
        // Parses as markup: escaping and palette tags are well-formed.
        _ = new Spectre.Console.Markup(Styled.Object("App.Weird[,]<X>", 1, 1));
    }

    [Theory]
    [InlineData("System.Collections.Generic.Dictionary<System.String, App.Order>+Entry[]", "System.Collections.Generic")]
    [InlineData("App.Program+<>c", "App")]
    [InlineData("App.Order[]", "App")]
    [InlineData("Order", "")]
    [InlineData("<PrivateImplementationDetails>", "")]
    [InlineData("Free", "")]
    public void NamespaceIsTheOutermostTypes(string name, string expected)
    {
        Assert.Equal(expected, TypeNames.Namespace(name));
    }

    [Fact]
    public void SortedRowsActivateTheirDataInsteadOfTheOldIndex()
    {
        var first = new Row("first", 2048, 0x1000);
        var second = new Row("second", 512, 0x2000);
        Row? activated = null;
        Table<Row> table = ViewFormatting.Table([first, second], row => activated = row,
            ViewFormatting.NameColumn<Row>("name", row => row.Name),
            ViewFormatting.BytesColumn<Row>("bytes", row => row.Bytes, Cellar.Primitives.Color.Default));

        table.SortBy(1, SortState.Ascending);
        ActivateFirst(table);
        Assert.Same(second, activated);
        table.SortBy(1, SortState.Descending);
        ActivateFirst(table);
        Assert.Same(first, activated);
    }

    [Fact]
    public void ReplacingFilteredRowsDoesNotRestoreStaleNavigation()
    {
        string? activated = null;
        Table<string> table = ViewFormatting.Table(["OldB", "OldA"], name => activated = name, Names());
        table.SortBy(0, SortState.Ascending);
        table.SetRows(["NewB", "NewA"]);
        table.SortBy(0, SortState.Descending);
        table.SortBy(0, SortState.None);

        Assert.Equal(2, table.Rows.Count);
        ActivateFirst(table);
        Assert.StartsWith("New", activated);
    }

    [Fact]
    public void EmptyRowsCannotActivateAStaleSelection()
    {
        bool activated = false;
        Table<string> table = ViewFormatting.Table([], _ => activated = true, Names());
        table.HasFocus = true;

        Assert.True(table.OnEvent(new KeyEvent(Key.Enter, default, default)));
        Assert.Equal(-1, table.SelectedIndex);
        Assert.False(activated);
    }

    [Fact]
    public void SortedHotMethodsKeepFullyQualifiedNavigation()
    {
        var profile = new AllocationProfile(
        [
            new(Sherlock.Tests.Frames.Stack("App.Zeta.Run"), "T", 2048, 8, 0, 0),
            new(Sherlock.Tests.Frames.Stack("Other.Alpha.Run"), "T", 512, 2, 0, 0),
        ]);
        NavigationTarget? activated = null;
        Table<AllocationMethodStat> table = AllocationsView.HotTable(profile.HotMethods(), profile.TotalAllocBytes, target => activated = target);
        table.SortBy(0, SortState.Ascending); // by method

        ActivateFirst(table);

        Assert.Equal(new MethodTarget(Sherlock.Tests.Frames.Method("Other.Alpha.Run")), activated);
    }

    [Fact]
    public void SortedAllocationTypesKeepTheRequestedLens()
    {
        var profile = new AllocationProfile(
        [
            new(Sherlock.Tests.Frames.Stack("Method"), "App.Zeta", 2048, 8, 0, 0),
            new(Sherlock.Tests.Frames.Stack("Method"), "Other.Alpha", 512, 2, 0, 0),
        ]);
        NavigationTarget? activated = null;
        Table<AllocationTypeStat> table = AllocationsView.TypeTable(profile, name => new TypeTarget(name, TypeTab.Allocations), target => activated = target);
        table.SortBy(2, SortState.Ascending); // by bytes allocated

        ActivateFirst(table);

        Assert.Equal(new TypeTarget("Other.Alpha", TypeTab.Allocations), activated);
    }

    [Fact]
    public void NavigationRequiresTypedPayloads()
    {
        var target = new ObjTarget(0x1234, ObjectTab.Roots);
        Assert.Same(target, NavigationTarget.FromLink(target));
        Assert.Throws<ArgumentException>(() => NavigationTarget.FromLink("0x1234"));
    }

    private static TableColumn<string> Names() => ViewFormatting.NameColumn<string>("name", name => name);

    private static void ActivateFirst<T>(Table<T> table)
    {
        table.SelectedIndex = 0;
        table.HasFocus = true;
        Assert.True(table.OnEvent(new KeyEvent(Key.Enter, default, default)));
    }

    private sealed record Row(string Name, long Bytes, ulong Address);
}
