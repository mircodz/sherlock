using System;
using System.Collections.Generic;
using System.Linq;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Terminal;
using Cellar.Theming;
using Cellar.Widgets;
using Sherlock.Core;
using Sherlock.Heap;
using static Sherlock.CLI.Tui.ViewFormatting;
using static System.FormattableString;

namespace Sherlock.CLI.Tui;

internal static class TypesView
{
    public static Widget Create(IReadOnlyList<HeapTypeStat> histogram, Action<NavigationTarget> navigate)
    {
        // Free space isn't a type; Health reports it.
        List<HeapTypeStat> types = histogram.Where(s => s.TypeName != "Free").OrderByDescending(s => s.TotalSize).ToList();
        long total = 1; // of the rows shown, so that the shares of a filtered list add up
        double Percent(HeapTypeStat row) => 100.0 * (long)row.TotalSize / total;
        Table<HeapTypeStat> table = Table<HeapTypeStat>([], row => navigate(new TypeTarget(row.TypeName)),
            NameColumn<HeapTypeStat>("Type", row => Rendering.DisplayNames.Type(row.TypeName)),
            NamespaceColumn<HeapTypeStat>(row => Rendering.DisplayNames.Namespace(row.TypeName)),
            BytesColumn<HeapTypeStat>("Bytes", row => (long)row.TotalSize, Theme.Current.Success),
            PercentColumn<HeapTypeStat>(Percent),
            CountColumn<HeapTypeStat>("Count", row => row.Count));

        void Populate(string query)
        {
            query = query.Trim();
            List<HeapTypeStat> rows = query.Length == 0
                ? types
                : types.Where(s => s.TypeName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            total = Math.Max(1, rows.Sum(s => (long)s.TotalSize));
            table.SetRows(rows);
        }
        Populate("");
        var filter = new Input { Placeholder = "/ to filter types by name\u2026", OnChange = Populate };
        return View("Types", new FilterStack(filter, table, Populate), "Enter instances  \u00b7  / filter");
    }

    public static Widget Instances(InstanceListing listing, string typeName, Action<NavigationTarget> navigate)
    {
        if (listing.Instances.Count == 0)
        {
            return Message("Instances", $"No live instances of {Rendering.DisplayNames.Type(typeName)} in this snapshot.");
        }
        string title = Invariant($"Instances \u00b7 {listing.TotalMatched:N0} \u00b7 {ByteFormat.Human(listing.TotalMatchedSize)}");
        if (listing.Instances.Count < listing.TotalMatched)
        {
            title += Invariant($" \u00b7 first {listing.Instances.Count:N0} shown");
        }
        Table<ObjectInstance> table = Table(listing.Instances, row => navigate(new ObjTarget(row.Address)),
            TextColumn<ObjectInstance>("Address", Constraint.Length(16), row => Rendering.Addresses.Format(row.Address), Theme.Current.Secondary),
            BytesColumn<ObjectInstance>("Size", row => (long)row.Size, Theme.Current.Success),
            TextColumn<ObjectInstance>("Preview", Constraint.Fill(), row => row.Preview ?? "", Theme.Current.Foreground));
        table.Columns[0].SortKey = row => row.Address;
        return View(title, table, "Enter inspect");
    }

    /// <summary>The filter box above the types table. The table has focus; '/' moves it to the filter, where Enter
    /// keeps the filter and Esc clears it, both returning to the table. Keys go to whichever has focus and no further,
    /// so Tab still switches views.</summary>
    private sealed class FilterStack : Widget
    {
        private const string FilteringPlaceholder = "type a name  \u00b7  Enter keeps the filter  \u00b7  Esc clears it";
        private readonly Input _input;
        private readonly Widget _body;
        private readonly Action<string> _apply;
        private readonly Stack _stack;
        private readonly string _idlePlaceholder;
        private bool _focused;
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
            get => _focused;
            set
            {
                _focused = value;
                _input.HasFocus = value && _filtering;
                _body.HasFocus = value && !_filtering;
            }
        }
        protected override void VisitChildren(Action<Widget> visit) => visit(_stack);
        public override Size Measure(Size available) => _stack.Measure(available);
        public override void Render(Cellar.Rendering.Surface surface, Rect area) => _stack.Render(surface, area);

        public override bool OnEvent(InputEvent input)
        {
            if (input is not KeyEvent key)
            {
                return _stack.OnEvent(input); // the mouse goes to what's under it
            }
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
            return (_filtering ? _input : _body).OnEvent(input);
        }

        private void SetFiltering(bool filtering)
        {
            _filtering = filtering;
            _input.Placeholder = filtering ? FilteringPlaceholder : _idlePlaceholder;
            HasFocus = true;
        }
    }
}
