using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Terminal;
using Cellar.Widgets;
using Sherlock.Core;
using static Sherlock.CLI.Tui.ViewFormatting;

namespace Sherlock.CLI.Tui;

internal static class TypesView
{
    public static Widget Create(IReadOnlyList<HeapTypeStat> histogram, Action<NavigationTarget> navigate)
    {
        List<HeapTypeStat> types = histogram.OrderByDescending(s => s.TotalSize).ToList();
        Table table = Table(("Type", Constraint.Fill(3), false), ("Count", Constraint.Length(11), true),
            ("Bytes", Constraint.Length(11), true), ("%", Constraint.Length(6), true), ("share", Constraint.Length(14), false));

        void Populate(string query)
        {
            query = query.Trim();
            List<HeapTypeStat> rows = query.Length == 0
                ? types
                : types.Where(s => s.TypeName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            long total = Math.Max(1, rows.Sum(s => (long)s.TotalSize));
            SetRows(table, rows, row =>
            {
                double percent = 100.0 * (long)row.TotalSize / total;
                return [row.TypeName, row.Count.ToString("N0", CultureInfo.InvariantCulture), ByteFormat.Human(row.TotalSize),
                    percent.ToString("0.0", CultureInfo.InvariantCulture), Bar(percent, 12)];
            }, row => navigate(new TypeTarget(row.TypeName)));
        }
        Populate("");
        var filter = new Input { Placeholder = "/ to filter types by name\u2026", OnChange = Populate };
        return Hinted(new Panel(new FilterStack(filter, table), " Types ") { BorderStyle = BorderStyle.Rounded },
            "Enter \u2192 instances  \u00b7  / filter  \u00b7  Esc unfocus  \u00b7  s sort  \u00b7  Backspace back");
    }

    public static Widget Instances(InstanceListing listing, string typeName, Action<NavigationTarget> navigate)
    {
        Table table = Table(("Address", Constraint.Length(16), false), ("Type", Constraint.Fill(2), false),
            ("Size", Constraint.Length(10), true), ("Preview", Constraint.Fill(3), false));
        SetRows(table, listing.Instances,
            row => [$"0x{row.Address:x}", TypeNames.Short(row.TypeName), ByteFormat.Human(row.Size), row.Preview ?? ""],
            row => navigate(new ObjTarget(row.Address)));
        return Hinted(new Panel(table, $" {TypeNames.Short(typeName)} \u2014 {listing.TotalMatched:N0} instances, {ByteFormat.Human(listing.TotalMatchedSize)} ") { BorderStyle = BorderStyle.Rounded },
            "Enter inspect  \u00b7  Backspace back");
    }

    private sealed class FilterStack : Widget
    {
        private readonly Input _input;
        private readonly Widget _body;
        private readonly Stack _stack;

        public FilterStack(Input input, Widget body)
        {
            _input = input;
            _body = body;
            _stack = new Stack(Direction.Vertical).Add(input, Constraint.Length(1)).Add(body, Constraint.Fill());
        }

        public override bool IsFocusable => true;
        public override bool HasFocus
        {
            get => _stack.HasFocus;
            set
            {
                _stack.HasFocus = value;
                if (value && !_input.HasFocus && !_body.HasFocus)
                {
                    _body.HasFocus = true;
                }
            }
        }
        protected override void VisitChildren(Action<Widget> visit) => visit(_stack);
        public override Size Measure(Size available) => _stack.Measure(available);
        public override void Render(Cellar.Rendering.Surface surface, Rect area) => _stack.Render(surface, area);

        public override bool OnEvent(InputEvent input)
        {
            if (input is KeyEvent key)
            {
                if (!_input.HasFocus && key.IsChar && key.Rune.Value == '/')
                {
                    _body.HasFocus = false;
                    _input.HasFocus = true;
                    return true;
                }
                if (_input.HasFocus && key.Key == Key.Escape)
                {
                    _input.HasFocus = false;
                    _body.HasFocus = true;
                    return true;
                }
            }
            return _stack.OnEvent(input);
        }
    }
}
