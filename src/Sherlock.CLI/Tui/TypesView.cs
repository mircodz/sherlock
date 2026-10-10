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
        // Free space isn't a type; Health reports it.
        List<HeapTypeStat> types = histogram.Where(s => s.TypeName != "Free").OrderByDescending(s => s.TotalSize).ToList();
        Table table = Table(("Type", Constraint.Fill(3), false), ("Count", Constraint.Length(11), true),
            ("Bytes", Constraint.Length(11), true), ("%", Constraint.Length(6), true), ("Share", Constraint.Length(14), false),
            ("Namespace", Constraint.Fill(2), false));

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
                return [TypeNames.Short(row.TypeName), row.Count.ToString("N0", CultureInfo.InvariantCulture), ByteFormat.Column(row.TotalSize),
                    percent.ToString("0.0", CultureInfo.InvariantCulture), Bar(percent, 12), TypeNames.Namespace(row.TypeName)];
            }, row => navigate(new TypeTarget(row.TypeName)));
        }
        Populate("");
        var filter = new Input { Placeholder = "/ to filter types by name\u2026", OnChange = Populate };
        return Hinted(new Panel(new FilterStack(filter, table, Populate), " Types ") { BorderStyle = BorderStyle.Rounded },
            "Enter instances  \u00b7  / filter  \u00b7  s sort");
    }

    public static Widget Instances(InstanceListing listing, string typeName, Action<NavigationTarget> navigate)
    {
        if (listing.Instances.Count == 0)
        {
            return Message($"No live instances of {TypeNames.Short(typeName)} in this snapshot.", $" {TypeNames.Short(typeName)} ");
        }
        string title = $" {TypeNames.Short(typeName)} \u2014 {listing.TotalMatched:N0} instances, {ByteFormat.Human(listing.TotalMatchedSize)} ";
        if (listing.Instances.Count < listing.TotalMatched)
        {
            title += $"\u00b7 showing the first {listing.Instances.Count:N0} ";
        }
        Table table = Table(("Address", Constraint.Length(16), false), ("Size", Constraint.Length(10), true),
            ("Preview", Constraint.Fill(), false));
        SetRows(table, listing.Instances,
            row => [Sherlock.CLI.Rendering.Addresses.Format(row.Address), ByteFormat.Column(row.Size), row.Preview ?? ""],
            row => navigate(new ObjTarget(row.Address)));
        return Hinted(new Panel(table, title) { BorderStyle = BorderStyle.Rounded }, "Enter inspect  \u00b7  s sort");
    }

    /// <summary>The filter box above the types table. The table has focus; '/' moves it to the filter, where Enter
    /// keeps the filter and Esc clears it, both returning to the table.</summary>
    private sealed class FilterStack : Widget
    {
        private const string FilteringPlaceholder = "type a name  \u00b7  Enter keeps the filter  \u00b7  Esc clears it";
        private readonly Input _input;
        private readonly Widget _body;
        private readonly Action<string> _apply;
        private readonly Stack _stack;
        private readonly string _idlePlaceholder;
        private bool _filtering;

        public FilterStack(Input input, Widget body, Action<string> apply)
        {
            _input = input;
            _body = body;
            _apply = apply;
            _idlePlaceholder = input.Placeholder;
            _stack = new Stack(Direction.Vertical).Add(input, Constraint.Length(1)).Add(body, Constraint.Fill());
        }

        public override bool IsFocusable => true;
        public override bool HasFocus
        {
            get => _stack.HasFocus;
            set
            {
                _stack.HasFocus = value; // focuses the stack's first child, the filter; correct that below
                _input.HasFocus = value && _filtering;
                _body.HasFocus = value && !_filtering;
            }
        }
        protected override void VisitChildren(Action<Widget> visit) => visit(_stack);
        public override Size Measure(Size available) => _stack.Measure(available);
        public override void Render(Cellar.Rendering.Surface surface, Rect area) => _stack.Render(surface, area);

        public override bool OnEvent(InputEvent input)
        {
            if (input is KeyEvent key)
            {
                if (!_filtering && key.IsChar && key.Rune.Value == '/')
                {
                    SetFiltering(true);
                    return true;
                }
                if (_filtering && key.Key is Key.Enter or Key.Escape)
                {
                    if (key.Key == Key.Escape)
                    {
                        _input.Text = "";
                        _apply("");
                    }
                    SetFiltering(false);
                    return true;
                }
            }
            return _stack.OnEvent(input);
        }

        private void SetFiltering(bool filtering)
        {
            _filtering = filtering;
            _input.Placeholder = filtering ? FilteringPlaceholder : _idlePlaceholder;
            HasFocus = true;
        }
    }
}
