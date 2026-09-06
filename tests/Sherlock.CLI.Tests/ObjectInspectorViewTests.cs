using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cellar.Primitives;
using Cellar.Rendering;
using Cellar.Terminal;
using Cellar.Widgets.Charts.Trees;
using Sherlock.CLI.Tui;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Xunit;

namespace Sherlock.CLI.Tests;

public sealed class ObjectInspectorViewTests
{
    private static ObjectValue Reference(string name, ulong address) =>
        new(name, "App.Node", $"0x{address:x} (App.Node)", ObjectValueKind.Reference, address, 32);

    private static KeyEvent KeyPress(Key key) => new(key, default, default);
    private static void Press(ObjectInspectorView view, Key key) => Assert.True(view.OnEvent(KeyPress(key)));

    [Fact]
    public void NestedObjectsLoadOnlyWhenExpandedAndStayCachedAfterCollapse()
    {
        var reads = new List<ulong>();
        var view = new ObjectInspectorView(Reference("$", 0x1000), (value, start, _, _) =>
        {
            reads.Add(value.Address!.Value);
            return value.Address == 0x1000
                ? new InspectionPage([Reference("child", 0x2000)], 1, start)
                : new InspectionPage([Reference("grandchild", 0x3000)], 1, start);
        }, _ => { }, _ => { }) { HasFocus = true };

        Assert.Equal(new ulong[] { 0x1000 }, reads);
        view.Render(new Surface(100, 20), new Rect(0, 0, 100, 20));
        Assert.Single(reads);

        Press(view, Key.Down);
        Press(view, Key.Right);
        Assert.Equal(new ulong[] { 0x1000, 0x2000 }, reads);
        Assert.Equal("$.child", view.Tree.SelectedNode!.Value.Path);
        Assert.Equal("$.child.grandchild", Assert.Single(view.Tree.SelectedNode.Children).Value.Path);

        Press(view, Key.Left);
        Press(view, Key.Right);
        Assert.Equal(2, reads.Count);
    }

    [Fact]
    public void EnterOpensAReferenceWithoutExpandingIt()
    {
        int reads = 0;
        ulong? opened = null;
        var view = new ObjectInspectorView(Reference("$", 0x1000), (_, start, _, _) =>
        {
            reads++;
            return new InspectionPage([Reference("child", 0x2000)], 1, start);
        }, address => opened = address, _ => { }) { HasFocus = true };

        Press(view, Key.Down);
        Press(view, Key.Enter);

        Assert.Equal(0x2000UL, opened);
        Assert.Equal(1, reads);
        Assert.False(view.Tree.SelectedNode!.IsExpanded);
    }

    [Fact]
    public void AncestorCyclesRemainLinkedButCannotExpand()
    {
        int reads = 0;
        ulong? opened = null;
        var view = new ObjectInspectorView(Reference("$", 0x1000), (_, start, _, _) =>
        {
            reads++;
            return new InspectionPage([Reference("self", 0x1000)], 1, start);
        }, address => opened = address, _ => { }) { HasFocus = true };
        TreeNode<ObjectInspectorView.Row> cycle = Assert.Single(view.Tree.Roots[0].Children);

        Assert.False(cycle.HasChildren);
        Assert.Equal("$", cycle.Value.CyclePath);
        Assert.Contains("cycle to $", ObjectInspectorView.RenderRow(cycle).PlainText);
        Assert.Contains(ObjectInspectorView.RenderRow(cycle).Spans, span => span.Link is ObjTarget { Address: 0x1000 });
        Press(view, Key.Down);
        Press(view, Key.Right);
        Assert.Equal(1, reads);
        Press(view, Key.Enter);
        Assert.Equal(0x1000UL, opened);
    }

    [Fact]
    public void SharedReferencesInSeparateBranchesAreNotCycles()
    {
        var view = new ObjectInspectorView(Reference("$", 0x1000), (_, start, _, _) =>
            new InspectionPage([Reference("left", 0x2000), Reference("right", 0x2000)], 2, start),
            _ => { }, _ => { });

        Assert.All(view.Tree.Roots[0].Children, child =>
        {
            Assert.Null(child.Value.CyclePath);
            Assert.True(child.HasChildren);
        });
    }

    [Fact]
    public void ManualExpansionHasNoFixedDepthLimit()
    {
        int reads = 0;
        var view = new ObjectInspectorView(Reference("$", 0x1000), (value, start, _, _) =>
        {
            reads++;
            return new InspectionPage([Reference("next", value.Address!.Value + 0x1000)], 1, start);
        }, _ => { }, _ => { }) { HasFocus = true };

        for (int depth = 0; depth < 128; depth++)
        {
            Press(view, Key.Down);
            Press(view, Key.Right);
        }

        Assert.Equal(129, reads);
        Assert.Null(view.Tree.SelectedNode!.Value.CyclePath);
        Assert.Equal("$" + string.Concat(Enumerable.Repeat(".next", 128)), view.Tree.SelectedNode.Value.Path);
    }

    [Fact]
    public void CollectionContinuationGroupsStaySiblingsAndUseBoundedPages()
    {
        var reads = new List<int>();
        var view = new ObjectInspectorView(Reference("$", 0x1000), (_, start, count, _) =>
        {
            Assert.Equal(64, count);
            reads.Add(start);
            ObjectValue[] items = Enumerable.Range(start, Math.Min(count, 130 - start))
                .Select(i => new ObjectValue($"[{i}]", "System.Int32", i.ToString(), ObjectValueKind.Number))
                .ToArray();
            return new InspectionPage(items, 130, start, 130, 256);
        }, _ => { }, _ => { }) { HasFocus = true };
        TreeNode<ObjectInspectorView.Row> root = view.Tree.Roots[0];

        Assert.Equal(65, root.Children.Count);
        Assert.Contains("count 130 / capacity 256", ObjectInspectorView.RenderRow(root).PlainText);
        Assert.False(root.Children[0].HasChildren);
        Press(view, Key.End);
        Press(view, Key.Enter);
        TreeNode<ObjectInspectorView.Row> secondPage = root.Children[64];
        Assert.Equal(64, secondPage.Children.Count);
        Assert.Equal("$[64]", secondPage.Children[0].Value.Path);
        Assert.Equal(66, root.Children.Count);
        Assert.Same(root, root.Children[65].Parent);

        Press(view, Key.End);
        Press(view, Key.Enter);
        Assert.Equal(2, root.Children[65].Children.Count);
        Assert.Equal(66, root.Children.Count);
        Assert.Equal(new[] { 0, 64, 128 }, reads);
    }

    [Fact]
    public void RawCollectionFieldsLoadSeparatelyAndKeepTheirPaths()
    {
        var reads = new List<bool>();
        var view = new ObjectInspectorView(Reference("$", 0x1000), (_, start, _, raw) =>
        {
            reads.Add(raw);
            return raw
                ? new InspectionPage([Reference("_items", 0x2000)], 1, start)
                : new InspectionPage([], 0, start, 0, 16) { HasRawFields = true };
        }, _ => { }, _ => { }) { HasFocus = true };

        Assert.Equal(new[] { false }, reads);
        Press(view, Key.End);
        Press(view, Key.Right);

        Assert.Equal(new[] { false, true }, reads);
        Assert.Equal("$._items", Assert.Single(view.Tree.SelectedNode!.Children).Value.Path);
    }

    [Fact]
    public void StructsExpandInlineButInteriorAddressesAreNotObjectLinks()
    {
        var structure = new ObjectValue("location", "App.Point", "{App.Point}", ObjectValueKind.Struct, 0x1010, MethodTable: 0x9000);
        ulong? opened = null;
        var view = new ObjectInspectorView(Reference("$", 0x1000), (value, start, _, _) =>
            value.Kind == ObjectValueKind.Struct
                ? new InspectionPage([new("X", "System.Int32", "42", ObjectValueKind.Number)], 1, start)
                : new InspectionPage([structure], 1, start), address => opened = address, _ => { }) { HasFocus = true };
        TreeNode<ObjectInspectorView.Row> child = Assert.Single(view.Tree.Roots[0].Children);

        Assert.DoesNotContain(ObjectInspectorView.RenderRow(child).Spans, span => span.Link is ObjTarget);
        Press(view, Key.Down);
        Press(view, Key.Enter);
        Assert.Null(opened);
        Assert.Equal("$.location.X", Assert.Single(child.Children).Value.Path);
    }

    [Theory]
    [InlineData(ObjectValueKind.Number)]
    [InlineData(ObjectValueKind.Boolean)]
    [InlineData(ObjectValueKind.Null)]
    [InlineData(ObjectValueKind.Pointer)]
    [InlineData(ObjectValueKind.Unreadable)]
    public void LeafValuesNeverBecomeObjectLinksFromTheirDisplayText(ObjectValueKind kind)
    {
        var value = new ObjectValue("field", "System.IntPtr", "0x1234 (App.Node)", kind);
        var node = new TreeNode<ObjectInspectorView.Row>(new ObjectInspectorView.Row(value));

        Assert.DoesNotContain(ObjectInspectorView.RenderRow(node).Spans, span => span.Link is ObjTarget);
        Assert.False(node.Value.CanExpand);
    }

    [Fact]
    public void StringsAndTypesHaveIndependentTypedLinks()
    {
        string? openedType = null;
        ulong? openedObject = null;
        var text = new ObjectValue("name", "System.String", "\"hello\\nworld\"", ObjectValueKind.String, 0x2000);
        var view = new ObjectInspectorView(Reference("$", 0x1000), (_, start, _, _) =>
            new InspectionPage([text], 1, start), address => openedObject = address, type => openedType = type)
        { HasFocus = true };
        TreeNode<ObjectInspectorView.Row> node = Assert.Single(view.Tree.Roots[0].Children);
        Cellar.Text.StyledText label = ObjectInspectorView.RenderRow(node);

        Assert.Contains("\"hello\\nworld\"", label.PlainText);
        Assert.DoesNotContain('\n', label.PlainText);
        Assert.Contains(label.Spans, span => span.Link is ObjTarget { Address: 0x2000 });
        Assert.Contains(label.Spans, span => span.Link is TypeTarget { Type: "System.String" });
        Assert.False(node.HasChildren);
        view.Tree.OnLinkClick!(new ObjTarget(0x2000));
        Assert.Equal(0x2000UL, openedObject);

        Press(view, Key.Down);
        Assert.True(view.OnEvent(new KeyEvent(Key.Char, new Rune('t'), default)));
        Assert.Equal("System.String", openedType);
    }

    [Fact]
    public void ScalarKindsHaveDistinctColorsWithoutLosingTheirValues()
    {
        var kinds = new[] { ObjectValueKind.String, ObjectValueKind.Number, ObjectValueKind.Null, ObjectValueKind.Unreadable };
        var colors = new HashSet<Color>();
        foreach (ObjectValueKind kind in kinds)
        {
            var value = new ObjectValue("value", "type", "preview", kind);
            var row = new TreeNode<ObjectInspectorView.Row>(new ObjectInspectorView.Row(value));
            Cellar.Text.Span preview = Assert.Single(ObjectInspectorView.RenderRow(row).Spans, span => span.Text == "preview");
            colors.Add(preview.Style.Foreground);
        }
        Assert.Equal(kinds.Length, colors.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedExpansionShowsAnErrorInsteadOfLosingTheView(bool invalidData)
    {
        var view = new ObjectInspectorView(Reference("$", 0x1000), (_, _, _, _) =>
        {
            if (invalidData)
            {
                throw new InvalidDataException("Cannot read this object");
            }
            throw new DumpAnalysisException("Cannot read this object");
        }, _ => { }, _ => { });

        TreeNode<ObjectInspectorView.Row> error = Assert.Single(view.Tree.Roots[0].Children);
        Assert.Equal(ObjectValueKind.Unreadable, error.Value.Value.Kind);
        Assert.Contains("Cannot read this object", ObjectInspectorView.RenderRow(error).PlainText);
    }

    [Fact]
    public void EmptyCollectionsAreExplicit()
    {
        var view = new ObjectInspectorView(Reference("$", 0x1000), (_, start, _, _) =>
            new InspectionPage([], 0, start, 0, 0), _ => { }, _ => { });

        Assert.Contains("(empty collection)", ObjectInspectorView.RenderRow(Assert.Single(view.Tree.Roots[0].Children)).PlainText);
    }
}
