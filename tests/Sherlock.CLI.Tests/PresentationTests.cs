using System;
using System.Globalization;
using Cellar.Layout;
using Cellar.Terminal;
using Cellar.Widgets;
using Sherlock.CLI.Rendering;
using Sherlock.CLI.Tui;
using Sherlock.Core;
using Sherlock.Core.Profiling;
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

    [Fact]
    public void ByteFormattingAndTableSortingAreCultureIndependent()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("1.5 KB", ByteFormat.Human(1536));
            Assert.Equal("1.5 KB", ByteSize.Format(1536));
            Assert.Equal(1536d, ViewFormatting.ParseNumber(ByteFormat.Human(1536)));
            Assert.Equal(12345d, ViewFormatting.ParseNumber("12,345"));
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
        Table table = ViewFormatting.Table(("name", Constraint.Fill(), false), ("bytes", Constraint.Length(10), true));
        var first = new Row("first", 2048, 0x1000);
        var second = new Row("second", 512, 0x2000);
        Row? activated = null;
        ViewFormatting.SetRows(table, new[] { first, second },
            row => [row.Name, ByteFormat.Human(row.Bytes)], row => activated = row);

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
        Table table = ViewFormatting.Table(("name", Constraint.Fill(), false));
        string? activated = null;
        ViewFormatting.SetRows(table, new[] { "OldB", "OldA" }, name => [name], name => activated = name);
        table.SortBy(0, SortState.Ascending);
        ViewFormatting.SetRows(table, new[] { "NewB", "NewA" }, name => [name], name => activated = name);
        table.SortBy(0, SortState.Descending);
        table.SortBy(0, SortState.None);

        Assert.Equal(2, table.Rows.Count);
        ActivateFirst(table);
        Assert.StartsWith("New", activated);
    }

    [Fact]
    public void EmptyRowsCannotActivateAStaleSelection()
    {
        Table table = ViewFormatting.Table(("name", Constraint.Fill(), false));
        bool activated = false;
        ViewFormatting.SetRows(table, Array.Empty<string>(), name => [name], _ => activated = true);
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
            new(["App.Zeta.Run"], 2048, 8, 0, 0),
            new(["Other.Alpha.Run"], 512, 2, 0, 0),
        ]);
        NavigationTarget? activated = null;
        Table table = AllocationsView.HotTable(profile, target => activated = target);
        table.SortBy(3, SortState.Ascending);

        ActivateFirst(table);

        Assert.Equal(new MethodTarget("Other.Alpha.Run"), activated);
    }

    [Fact]
    public void SortedAllocationTypesKeepTheRequestedLens()
    {
        var profile = new AllocationProfile(
        [
            new(["Method"], 2048, 8, 0, 0, "App.Zeta"),
            new(["Method"], 512, 2, 0, 0, "Other.Alpha"),
        ]);
        NavigationTarget? activated = null;
        Table table = AllocationsView.TypeTable(profile, name => new TypeTarget(name, TypeTab.Allocations), target => activated = target);
        table.SortBy(1, SortState.Ascending);

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

    private static void ActivateFirst(Table table)
    {
        table.SelectedIndex = 0;
        table.HasFocus = true;
        Assert.True(table.OnEvent(new KeyEvent(Key.Enter, default, default)));
    }

    private sealed record Row(string Name, long Bytes, ulong Address);
}
