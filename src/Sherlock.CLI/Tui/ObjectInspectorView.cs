using System;
using System.Collections.Generic;
using System.IO;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Rendering;
using Cellar.Terminal;
using Cellar.Text;
using Cellar.Widgets;
using Cellar.Widgets.Charts.Trees;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Palette = Sherlock.CLI.Rendering.Theme;
using Theme = Cellar.Theming.Theme;

namespace Sherlock.CLI.Tui;

internal sealed class ObjectInspectorView : Widget
{
    private const int PageSize = 64;
    private readonly Func<ObjectValue, int, int, bool, InspectionPage> _readChildren;
    private readonly Action<ulong> _openObject;
    private readonly Action<string> _openType;
    private readonly TreeView<Row> _tree;
    private readonly Label _path = new(StyledText.Empty());
    private readonly Widget _body;

    internal TreeView<Row> Tree => _tree;

    public ObjectInspectorView(ObjectValue root, Func<ObjectValue, int, int, bool, InspectionPage> readChildren,
        Action<ulong> openObject, Action<string> openType)
    {
        _readChildren = readChildren;
        _openObject = openObject;
        _openType = openType;
        _tree = new TreeView<Row>
        {
            RenderLabel = RenderRow,
            ShowGuides = true,
            Striped = true,
            OnSelect = _ => UpdatePath(),
            OnLinkClick = Follow,
        };

        TreeNode<Row> node = CreateNode(new Row(root));
        _tree.AddRoot(node);
        node.Expand();
        LoadExpanded(node);
        UpdatePath();

        _body = new Stack(Direction.Vertical)
            .Add(new Padding(_path, new Thickness(1, 0)), Constraint.Length(1))
            .Add(new Panel(_tree, " Inspect - fields and contents ") { BorderStyle = BorderStyle.Rounded }, Constraint.Fill())
            .Add(new Label(new StyledText(" Right/Left expand/collapse  Enter open  t type  Tab views  Backspace back", Theme.Current.MutedStyle)), Constraint.Length(1));
    }

    public override bool IsFocusable => true;
    public override bool HasFocus { get => _tree.HasFocus; set => _tree.HasFocus = value; }
    protected override void VisitChildren(Action<Widget> visit) => visit(_body);
    public override Size Measure(Size available) => _body.Measure(available);
    public override void Render(Surface surface, Rect area) => _body.Render(surface, area);

    public override bool OnEvent(InputEvent input)
    {
        if (HasFocus && input is KeyEvent key && _tree.SelectedNode is { } selected)
        {
            Row row = selected.Value;
            if (key.Key == Key.Enter && row.Kind == RowKind.Value && row.Parent is not null &&
                ObjectAddress(row.Value) is { } address)
            {
                _openObject(address);
                return true;
            }
            if (key.IsChar && key.Rune.Value == 't' && row.Kind == RowKind.Value)
            {
                _openType(row.Value.TypeName);
                return true;
            }
        }

        bool handled = _tree.OnEvent(input);
        if (handled && _tree.SelectedNode is { } node)
        {
            LoadExpanded(node);
            UpdatePath();
        }
        return handled;
    }

    private static TreeNode<Row> CreateNode(Row row) => new(row) { HasChildrenHint = row.CanExpand };

    private void LoadExpanded(TreeNode<Row> node)
    {
        Row row = node.Value;
        if (!node.IsExpanded || row.Loaded || !row.CanExpand)
        {
            return;
        }
        row.Loaded = true;

        InspectionPage page;
        try
        {
            page = _readChildren(row.Value, row.StartIndex, PageSize, row.Raw);
        }
        catch (Exception ex) when (ex is DumpAnalysisException or IOException or InvalidDataException)
        {
            node.AddChild(CreateNode(new Row(
                new ObjectValue("error", "", TextUtil.Preview(ex.Message, 256), ObjectValueKind.Unreadable), row)));
            _tree.MarkDirty();
            return;
        }

        row.CollectionCount = page.CollectionCount;
        row.Capacity = page.Capacity;
        row.EndIndex = page.NextIndex;
        Row owner = row.Kind == RowKind.Value ? row : row.Parent!;
        foreach (ObjectValue value in page.Items)
        {
            node.AddChild(CreateNode(new Row(value, owner)));
        }
        if (page.Items.Count == 0)
        {
            string message = page.CollectionCount is not null ? "(empty collection)" : "(no fields)";
            node.AddChild(CreateNode(new Row(new ObjectValue("", "", message, ObjectValueKind.Other), owner)));
        }
        if (page.HasRawFields && row.Kind == RowKind.Value)
        {
            node.AddChild(CreateNode(new Row(row.Value, owner, RowKind.RawFields, raw: true)));
        }
        if (page.HasMore)
        {
            // Continuation groups stay siblings, so paging a wide collection does not deepen the tree.
            TreeNode<Row> pagingParent = row.Kind == RowKind.More ? node.Parent! : node;
            pagingParent.AddChild(CreateNode(new Row(row.Value, owner, RowKind.More, page.NextIndex, row.Raw)
            {
                TotalCount = page.TotalCount,
            }));
        }
        _tree.MarkDirty();
    }

    private void Follow(object target)
    {
        if (target is ulong address)
        {
            _openObject(address);
        }
        else if (target is string type)
        {
            _openType(type);
        }
    }

    private void UpdatePath()
    {
        if (_tree.SelectedNode is not { } node)
        {
            return;
        }
        Row row = node.Value;
        StyledText text = StyledText.Of(row.Path).Fg(Theme.Current.Foreground);
        if (row.Value.TypeName.Length > 0)
        {
            text.Append(" : ").Fg(Theme.Current.Muted)
                .Append(row.Value.TypeName).Fg(Color.Hex(Palette.Identity));
        }
        if (row.Value.Offset is { } offset && row.Kind == RowKind.Value)
        {
            text.Append($"  offset +0x{offset:x}").Fg(Theme.Current.Muted);
        }
        _path.Content = text;
    }

    internal static StyledText RenderRow(TreeNode<Row> node)
    {
        Row row = node.Value;
        Theme theme = Theme.Current;
        if (row.Kind == RowKind.RawFields)
        {
            return StyledText.Of("Raw fields").Fg(theme.Accent).Append("  (backing storage)").Fg(theme.Muted);
        }
        if (row.Kind == RowKind.More)
        {
            string label = row.Loaded
                ? $"[{row.StartIndex}..{Math.Max(row.StartIndex, row.EndIndex - 1)}]"
                : $"Load next {Math.Min(PageSize, row.TotalCount - row.StartIndex)}  ({row.StartIndex:N0} of {row.TotalCount:N0} shown)";
            return StyledText.Of(label).Fg(theme.Accent);
        }

        ObjectValue value = row.Value;
        StyledText text = StyledText.Of(value.Name).Fg(theme.Foreground);
        if (value.TypeName.Length > 0)
        {
            text.Append(" : ").Fg(theme.Muted)
                .Append(TypeNames.Short(value.TypeName)).Fg(Color.Hex(Palette.Identity)).Underline().Link(value.TypeName);
        }
        if (ObjectAddress(value) is { } address)
        {
            text.Append("  @").Fg(theme.Muted)
                .Append($"0x{address:x}").Fg(Color.Hex(Palette.Address)).Underline().Link(address);
        }
        if (value.Kind is not (ObjectValueKind.Reference or ObjectValueKind.Struct))
        {
            Color color = value.Kind switch
            {
                ObjectValueKind.String or ObjectValueKind.Character => Color.Hex(Palette.Focus),
                ObjectValueKind.Number => Color.Hex(Palette.Magenta),
                ObjectValueKind.Boolean or ObjectValueKind.Pointer => Color.Hex(Palette.Address),
                ObjectValueKind.Null => theme.Muted,
                ObjectValueKind.Unreadable => theme.Error,
                _ => theme.Foreground,
            };
            text.Append(" = ").Fg(theme.Muted).Append(value.Value).Fg(color);
        }
        if (value.Size is { } bytes)
        {
            text.Append($"  {ByteFormat.Human((long)bytes)} shallow").Fg(theme.Muted);
        }
        if (row.CollectionCount is { } count)
        {
            text.Append($"  count {count:N0}").Fg(theme.Foreground);
            if (row.Capacity is { } capacity)
            {
                text.Append($" / capacity {capacity:N0}").Fg(theme.Muted);
            }
        }
        if (row.CyclePath is { } cycle)
        {
            text.Append($"  (cycle to {cycle})").Fg(theme.Warning);
        }
        return text;
    }

    private static ulong? ObjectAddress(ObjectValue value) =>
        value.Kind is ObjectValueKind.Reference or ObjectValueKind.String ? value.Address : null;

    internal enum RowKind { Value, More, RawFields }

    internal sealed class Row
    {
        public Row(ObjectValue value, Row? parent = null, RowKind kind = RowKind.Value, int startIndex = 0, bool raw = false)
        {
            Value = value;
            Parent = parent;
            Kind = kind;
            StartIndex = startIndex;
            Raw = raw;
            if (kind == RowKind.Value && ObjectAddress(value) is { } address)
            {
                for (Row? ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
                {
                    if (ObjectAddress(ancestor.Value) == address)
                    {
                        CyclePath = ancestor.Path;
                        break;
                    }
                }
            }
        }

        public ObjectValue Value { get; }
        public Row? Parent { get; }
        public RowKind Kind { get; }
        public int StartIndex { get; }
        public bool Raw { get; }
        public string? CyclePath { get; }
        public bool Loaded { get; set; }
        public int EndIndex { get; set; }
        public int TotalCount { get; init; }
        public int? CollectionCount { get; set; }
        public int? Capacity { get; set; }
        public bool CanExpand => Kind != RowKind.Value || Value.CanExpand && CyclePath is null;

        public string Path
        {
            get
            {
                var parts = new List<string>();
                for (Row? row = this; row is not null; row = row.Parent)
                {
                    if (row.Kind == RowKind.Value)
                    {
                        parts.Add(row.Value.Name);
                    }
                }
                parts.Reverse();
                var path = new System.Text.StringBuilder();
                foreach (string part in parts)
                {
                    if (path.Length > 0 && !part.StartsWith('['))
                    {
                        path.Append('.');
                    }
                    path.Append(part);
                }
                return path.ToString();
            }
        }
    }
}
