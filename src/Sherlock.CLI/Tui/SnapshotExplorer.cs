using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Terminal;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Cellar.Widgets.Charts;
using Cellar.Widgets.Charts.Trees;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Sherlock.Core.Diagnostics;
using Sherlock.Core.Profiling;
using Sherlock.Core.Store;

namespace Sherlock.CLI.Tui;

// A Sherlock heap explorer on Cellar, hosted in the CLI as `sl tui`. A Navigator drill-down
// (Snapshots -> Snapshot workspace -> Instances -> Instance detail); the workspace is a Tabs "lens"
// bar: Health · Types · Retention · Allocations. See docs/design/tui.md.
//
// Links make the heap a walkable graph. Any address / type / method rendered in a tree carries a
// navigation intent (ObjTarget / TypeTarget / MethodTarget) as its link payload; clicking routes
// through Follow, which opens the most relevant panel:
//   address -> the object's detail (Inspect, or GC roots when the context is "why is this alive")
//   type    -> that type's instances
//   method  -> that method's allocation callers
// A row's Enter (OnActivate) hits the same router as a mouse click on its link.
public static class SnapshotExplorer
{
    public static async Task<int> Run()
    {
        Sherlock.CLI.Rendering.Theme.ApplyCellar();
        var store = SnapshotStore.Default();

        var entries = store.Sessions
            .SelectMany(s => s.Processes.SelectMany(p => p.Snapshots.Select(snap => (Process: p, Snap: snap))))
            .Where(x => x.Snap.Exists)
            .OrderByDescending(x => x.Snap.CreatedAt)
            .ToList();

        if (entries.Count == 0)
        {
            Console.Error.WriteLine("No snapshots in the library. Capture one with `sl collect` or `sl run` first.");
            return 1;
        }

        var nav = new Navigator { BackKey = Key.Backspace };
        Snapshot? current = null;

        Color[] palette = Sherlock.CLI.Rendering.Theme.ChartColors();

        // The one router: a link payload (or an Enter on a row) says where to go; we nest a focused page.
        void Follow(Snapshot snap, object payload)
        {
            switch (payload)
            {
                case ObjTarget o: nav.Push(new Page($"0x{o.Address:x}", InstancePage(snap, o.Address, o.Tab))); break;
                case TypeTarget t: nav.Push(new Page(Short(t.Type), TypeDetailPage(snap, t.Type, t.Tab))); break;
                case MethodTarget m: nav.Push(new Page(Short(m.Method), MethodPage(snap, m.Method))); break;
            }
        }

        // ---- the snapshot workspace (the lens tabs) ----

        // Lazy, async lenses: each tab runs its analysis (histogram / dominators / allocation profile)
        // only when first shown, wrapped in AsyncContent so the tab paints a spinner instantly and swaps
        // in the real widget when the background factory completes. A lens never opened never runs.
        Widget Lens(Func<Widget> build) => new AsyncContent(_ => build());
        // Token-aware variant: threads AsyncContent's cancellation (fired when the user navigates away)
        // into the analysis, so an expensive query like gcroot stops instead of running to completion.
        Widget LensCt(Func<CancellationToken, Widget> build) => new AsyncContent(ct => build(ct));

        Widget Workspace(Snapshot snap, string id) =>
            new Tabs()
                .Add("Health", () => Lens(() => HealthTab(snap, id)))
                .Add("Types", () => Lens(() => TypesTab(snap)))
                .Add("Retention", () => Lens(() => RetentionTab(snap)))
                .Add("Allocations", () => Lens(() => AllocationsTab(snap)));

        // The Health lens's "what looks wrong", computed inline from the two cheap, already-loaded
        // analyses (dominator tree + graph histogram). Deliberately skips the full `doctor` sweep,
        // whose extra ClrMD walks (duplicate strings, event-handler leaks) cost seconds.
        List<Finding> QuickFindings(Snapshot snap)
        {
            var result = new List<Finding>();

            // Biggest retained graph, where a leak concentrates.
            DominatorTree tree = snap.Dominators;
            ulong reachable = tree.TotalReachableBytes;
            if (reachable > 0 && tree.TopDominators(1).FirstOrDefault() is { } top)
            {
                double pct = 100.0 * top.RetainedSize / reachable;
                if (pct >= 10)
                {
                    bool coll = top.TypeName.Contains("List<") || top.TypeName.Contains("Dictionary<") ||
                                top.TypeName.Contains("HashSet<") || top.TypeName.Contains("[]");
                    result.Add(new Finding(
                        pct >= 50 ? FindingSeverity.High : FindingSeverity.Warning, "retention",
                        $"{Short(top.TypeName)} {(coll ? "collection " : "")}retains {ByteFormat.Human((long)top.RetainedSize)} ({pct:0}% of the reachable heap)",
                        "The biggest retained graph — where a leak concentrates.")
                    {
                        Address = top.Address,
                        Type = top.TypeName,
                        NextCommand = $"gcroot 0x{top.Address:x}",
                    });
                }
            }

            List<HeapTypeStat> byType = snap.Histogram.OrderByDescending(s => s.TotalSize).ToList();
            long heapBytes = byType.Sum(s => (long)s.TotalSize);

            // Fragmentation: a high free-space ratio.
            HeapTypeStat? free = byType.FirstOrDefault(s => s.TypeName == "Free");
            if (free is not null && heapBytes > 0 && 100.0 * free.TotalSize / heapBytes is var fpct && fpct >= 25)
            {
                result.Add(new Finding(FindingSeverity.Warning, "fragmentation",
                    $"Heap fragmentation: {ByteFormat.Human((long)free.TotalSize)} free ({fpct:0}% of heap)",
                    "A high free-space ratio means fragmentation (or a large collection just ran)."));
            }

            // Unbounded growth: a user type with a huge instance count.
            HeapTypeStat? grow = byType.FirstOrDefault(s => s.Count >= 10_000 && s.TypeName != "Free"
                && !s.TypeName.StartsWith("System.", StringComparison.Ordinal)
                && !s.TypeName.StartsWith("Microsoft.", StringComparison.Ordinal));
            if (grow is not null)
            {
                result.Add(new Finding(FindingSeverity.Info, "growth",
                    $"{grow.Count:N0} instances of {Short(grow.TypeName)} ({ByteFormat.Human((long)grow.TotalSize)})",
                    "A large population — check for unbounded growth (a cache or list that never shrinks).")
                {
                    Type = grow.TypeName,
                    NextCommand = $"objects {Short(grow.TypeName)}",
                });
            }

            return result.OrderBy(f => f.Severity).ToList();
        }

        // Health: heap composition (proportion bar + legend) and the doctor's findings; each finding's
        // `→ command` is a link that jumps to the object / type it's about.
        Widget HealthTab(Snapshot snap, string id)
        {
            List<HeapTypeStat> hist = snap.Histogram.OrderByDescending(s => s.TotalSize).ToList();
            long total = Math.Max(1, hist.Sum(s => (long)s.TotalSize));

            var bar = new ProportionBar();
            var legend = new Legend { Horizontal = true };
            long shown = 0;
            for (int i = 0; i < Math.Min(6, hist.Count); i++)
            {
                Color color = palette[i % palette.Length];
                long bytes = (long)hist[i].TotalSize;
                bar.Segments.Add(new Segment(Short(hist[i].TypeName), bytes, color));
                legend.Items.Add(new LegendItem(Short(hist[i].TypeName), color, $"{100.0 * bytes / total:0}%"));
                shown += bytes;
            }
            if (total > shown)
            {
                bar.Segments.Add(new Segment("other", total - shown, Theme.Current.Muted));
                legend.Items.Add(new LegendItem("other", Theme.Current.Muted, $"{100.0 * (total - shown) / total:0}%"));
            }

            var findings = new Stack(Direction.Vertical);
            foreach (Finding f in QuickFindings(snap))
            {
                (string glyph, Color color) = f.Severity switch
                {
                    FindingSeverity.High => ("●", Theme.Current.Error),
                    FindingSeverity.Warning => ("●", Theme.Current.Warning),
                    _ => ("○", Theme.Current.Muted),
                };
                findings.Add(new Label(StyledText.Of($"{glyph} ").Fg(color).Append(f.Title).Fg(Theme.Current.Foreground)), Constraint.Length(1));
                findings.Add(new Label(new StyledText("   " + f.Detail, Theme.Current.MutedStyle)), Constraint.Length(1));

                object? target = f.Address is ulong fa
                    ? new ObjTarget(fa, f.Category.Contains("event", StringComparison.OrdinalIgnoreCase) ? "Roots" : "Inspect")
                    : f.Type is { } ft ? new TypeTarget(ft) : null;
                if (f.NextCommand is { } next)
                {
                    StyledText st = StyledText.Of("   → ").Fg(Theme.Current.Muted).Append(next).Fg(Theme.Current.Accent);
                    if (target is not null)
                    {
                        st = st.Underline().Link(target);
                    }

                    var line = new Label(st);
                    if (target is not null)
                    {
                        line.OnLinkClick = p => Follow(snap, p);
                    }

                    findings.Add(line, Constraint.Length(1));
                }
            }

            var body = new Stack(Direction.Vertical)
                .Add(new Label(StyledText.Empty()), Constraint.Length(1))
                .Add(new Label(StyledText.Of("Heap composition").Bold().Fg(Theme.Current.Accent)), Constraint.Length(1))
                .Add(bar, Constraint.Length(1))
                .Add(legend, Constraint.Length(1))
                .Add(new Label(StyledText.Empty()), Constraint.Length(1))
                .Add(new Label(StyledText.Of("What looks wrong").Bold().Fg(Theme.Current.Accent)), Constraint.Length(1))
                .Add(new ScrollView(findings), Constraint.Fill());

            return Hinted(new Panel(new Padding(body, new Thickness(1, 0)), $" {id} — {ByteFormat.Human(total)} on the heap ") { BorderStyle = BorderStyle.Rounded },
                "click a → to jump  ·  Tab switch lens  ·  q quit");
        }

        // Types: histogram with a per-row share bar; Enter (or a click) drills into a type's instances.
        // A `/` filter prompt narrows the list live (case-insensitive substring on the type name).
        Widget TypesTab(Snapshot snap)
        {
            List<HeapTypeStat> hist = snap.Histogram.OrderByDescending(s => s.TotalSize).ToList();
            Table t = MakeTable(("Type", Constraint.Fill(3), false), ("Count", Constraint.Length(11), true),
                ("Bytes", Constraint.Length(11), true), ("%", Constraint.Length(6), true), ("share", Constraint.Length(14), false));

            void Populate(string query)
            {
                query = query.Trim();
                List<HeapTypeStat> rows = query.Length == 0
                    ? hist
                    : hist.Where(s => s.TypeName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
                long total = Math.Max(1, rows.Sum(s => (long)s.TotalSize));
                t.Rows.Clear();
                foreach (HeapTypeStat s in rows)
                {
                    double pct = 100.0 * (long)s.TotalSize / total;
                    t.Rows.Add([s.TypeName, s.Count.ToString("N0", CultureInfo.InvariantCulture), ByteFormat.Human((long)s.TotalSize), pct.ToString("0.0", CultureInfo.InvariantCulture), Bar(pct, 12)]);
                }
                t.SelectedIndex = t.Rows.Count > 0 ? 0 : -1;
                t.ScrollOffset = 0;
            }
            Populate("");
            t.OnActivate = i => Follow(snap, new TypeTarget(t.Rows[i][0]));

            var filter = new Input { Placeholder = "/ to filter types by name…", OnChange = Populate };
            var body = new FilterStack(filter, t);
            return Hinted(new Panel(body, " Types ") { BorderStyle = BorderStyle.Rounded },
                "Enter → instances  ·  / filter  ·  Esc unfocus  ·  s sort  ·  Backspace back");
        }

        // Retention: the dominator tree. Roots ARE the top dominator objects; children come lazily from
        // the dominator tree. Type and address are both links (type → instances, address → inspect).
        Widget RetentionTab(Snapshot snap)
        {
            DominatorTree dom = snap.Dominators;
            long total = Math.Max(1, (long)dom.TotalReachableBytes);

            var tree = new TreeView<DominatorNode>
            {
                RenderLabel = n => StyledText.Of(Short(n.Value.TypeName)).Fg(Theme.Current.Accent).Underline().Link(new TypeTarget(n.Value.TypeName))
                    .Append("  @").Fg(Theme.Current.Muted)
                    .Append($"0x{n.Value.Address:x}").Fg(Theme.Current.Secondary).Underline().Link(new ObjTarget(n.Value.Address)),
                ShowHeader = true,
                ShowGuides = true,
                Striped = true,
            };
            tree.Columns.Add(new TreeColumn<DominatorNode>("Retained", 11, n => new StyledText(ByteFormat.Human((long)n.Value.RetainedSize), new Style(Theme.Current.Success, Color.Default))));
            tree.Columns.Add(new TreeColumn<DominatorNode>("%", 6, n =>
            {
                double p = 100.0 * (long)n.Value.RetainedSize / total;
                Color c = p >= 10 ? Theme.Current.Success : Theme.Current.Muted;
                return new StyledText(p.ToString("0.0", CultureInfo.InvariantCulture), new Style(c, Color.Default));
            }));
            tree.Columns.Add(new TreeColumn<DominatorNode>("Own", 10, n => new StyledText(ByteFormat.Human((long)n.Value.OwnSize), Theme.Current.MutedStyle)));

            foreach (DominatorNode node in dom.TopDominators(25))
            {
                tree.AddRoot(node, parent => dom.ImmediateChildren(parent.Address, 20));
            }
            tree.OnLinkClick = p => Follow(snap, p);
            tree.OnActivate = n => Follow(snap, new ObjTarget(n.Value.Address));

            return Hinted(new Panel(tree, " Retention — what holds the memory ") { BorderStyle = BorderStyle.Rounded },
                "→/← expand   ·   click a type or address   ·   Enter inspect   ·   Backspace back");
        }

        // Allocations: two axes over the same profile. `By type` (needs a v2 profile) leads;
        // `Call tree` is the flow flamegraph; `Hot methods` the flat companion.
        Widget AllocationsTab(Snapshot snap)
        {
            if (snap.Allocations is not { } profile)
            {
                return new Panel(new Padding(new Label(new StyledText(
                    "No allocation profile in this snapshot. Capture with `run --profile` or `run --correlate`.", Theme.Current.MutedStyle)), new Thickness(1)),
                    " Allocations ")
                { BorderStyle = BorderStyle.Rounded };
            }

            // Inner tabs are lazy too: the call-tree flamegraph and hot-methods table each fold the
            // whole profile, so they build only when their subtab is shown.
            var tabs = new Tabs();
            if (profile.HasTypes)
            {
                tabs.Add("By type", () => ByTypeTab(snap, profile));
            }
            tabs.Add("Call tree", () =>
            {
                AllocationTreeNode rootNode = AllocationTreeNode.Build(profile);
                var flame = new FlameGraph<AllocationTreeNode> { LabelSelector = n => n.Label, Root = ToFlame(rootNode, "all allocations") };
                return FlamePanel(flame, rootNode.AllocBytes, " Allocation flow — width = bytes ",
                    "click a frame to zoom · Backspace zooms out · ↑↓←→ navigate");
            })
            .Add("Hot methods", () => Hinted(new Panel(HotTable(profile, m => Follow(snap, new MethodTarget(m))), " Hot allocation sites ") { BorderStyle = BorderStyle.Rounded },
                "Enter → callers · s sort · Backspace back"));
            return tabs;
        }

        // By type: what was allocated, largest churn first, with survived share and origin concentration.
        // At the top level, Enter → that type's call tree (where it came from).
        Widget ByTypeTab(Snapshot snap, AllocationProfile profile) =>
            Hinted(new Panel(TypeTable(snap, profile, name => new TypeTarget(name, "Call tree")), " Allocations by type — what the program allocated ") { BorderStyle = BorderStyle.Rounded },
                "Enter → where this type came from  ·  s sort  ·  Backspace back");

        // A type-breakdown table over a profile: allocated / survived / count / #sites / share. `onType`
        // decides where a row drills: the origin call tree at the top level, or live instances inside a method.
        Table TypeTable(Snapshot snap, AllocationProfile profile, Func<string, object> onType)
        {
            long total = Math.Max(1, profile.TotalAllocBytes);
            Table t = MakeTable(("Type", Constraint.Fill(3), false), ("Allocated", Constraint.Length(11), true),
                ("Survived", Constraint.Length(11), true), ("Count", Constraint.Length(10), true),
                ("Sites", Constraint.Length(6), true), ("share", Constraint.Length(12), false));
            var names = new List<string>();
            foreach (AllocationTypeStat s in profile.ByType())
            {
                names.Add(s.TypeName);
                t.Rows.Add([s.TypeName, ByteFormat.Human(s.AllocBytes), ByteFormat.Human(s.SurvivedBytes),
                    s.AllocCount.ToString("N0", CultureInfo.InvariantCulture), s.SiteCount.ToString(CultureInfo.InvariantCulture),
                    Bar(100.0 * s.AllocBytes / total, 10)]);
            }
            t.OnActivate = i => Follow(snap, onType(names[i]));
            return t;
        }

        // A type's detail page: its live Instances and the Call tree of where it was allocated,
        // as lazy tabs. `tab` picks which one a link wanted to land on.
        Widget TypeDetailPage(Snapshot snap, string typeName, string tab = "Instances")
        {
            var tabs = new Tabs()
                .Add("Instances", () => InstancesPage(snap, typeName))
                .Add("Call tree", () => TypeAllocTab(snap, typeName));
            tabs.ActiveIndex = tab == "Call tree" ? 1 : 0;
            return Hinted(tabs, "Tab / ←→ switch view  ·  Enter drill in  ·  Backspace back");
        }

        // Where a given type was allocated: the call tree of just that type's sites.
        Widget TypeAllocTab(Snapshot snap, string typeName)
        {
            if (snap.Allocations is not { } profile)
            {
                return new Panel(new Padding(new Label(new StyledText(
                    "No allocation profile in this snapshot. Capture with `run --profile` or `run --correlate`.", Theme.Current.MutedStyle)), new Thickness(1)),
                    " Call tree ")
                { BorderStyle = BorderStyle.Rounded };
            }
            AllocationTreeNode root = AllocationTreeNode.Build(profile.OfType(typeName));
            return Hinted(new Panel(AllocTreeTable(snap, root.Children, root.AllocBytes), $" Where {Short(typeName)} came from — {ByteFormat.Human(root.AllocBytes)} ") { BorderStyle = BorderStyle.Rounded },
                "→/← expand · collapse   ·   click a frame → its callers   ·   Backspace back");
        }

        // A call-tree tree-table over allocation nodes: per-frame allocated / % / survived / count,
        // roots = the given top frames, children lazy + expanded.
        Widget AllocTreeTable(Snapshot snap, IEnumerable<AllocationTreeNode> roots, long total)
        {
            long denom = Math.Max(1, total);
            var tree = new TreeView<AllocationTreeNode>
            {
                RenderLabel = n => StyledText.Of(Short(n.Value.Frame)).Fg(Theme.Current.Accent).Underline().Link(new MethodTarget(n.Value.Frame)),
                ShowHeader = true,
                ShowGuides = true,
                Striped = true,
            };
            tree.Columns.Add(new TreeColumn<AllocationTreeNode>("Allocated", 11, n => new StyledText(ByteFormat.Human(n.Value.AllocBytes), new Style(Theme.Current.Success, Color.Default))));
            tree.Columns.Add(new TreeColumn<AllocationTreeNode>("%", 6, n =>
            {
                double p = 100.0 * n.Value.AllocBytes / denom;
                Color c = p >= 10 ? Theme.Current.Success : Theme.Current.Muted;
                return new StyledText(p.ToString("0.0", CultureInfo.InvariantCulture), new Style(c, Color.Default));
            }));
            tree.Columns.Add(new TreeColumn<AllocationTreeNode>("Survived", 11, n => new StyledText(ByteFormat.Human(n.Value.SurvivedBytes), new Style(Theme.Current.Foreground, Color.Default))));
            tree.Columns.Add(new TreeColumn<AllocationTreeNode>("Count", 10, n => new StyledText(n.Value.AllocCount.ToString("N0", CultureInfo.InvariantCulture), Theme.Current.MutedStyle)));

            foreach (AllocationTreeNode node in roots)
            {
                TreeNode<AllocationTreeNode> r = tree.AddRoot(node, n => n.Children);
                r.ExpandAll();
            }
            tree.OnLinkClick = p => Follow(snap, p);
            tree.OnActivate = n => Follow(snap, new MethodTarget(n.Value.Frame));
            tree.MarkDirty();
            return tree;
        }

        // A flamegraph with a live info bar above it: zoom breadcrumb + the selected frame's bytes,
        // share of total, and object count. Updated on select/zoom.
        Widget FlamePanel(FlameGraph<AllocationTreeNode> flame, long total, string title, string hint)
        {
            var info = new Label(StyledText.Empty());
            void ShowInfo()
            {
                FlameNode<AllocationTreeNode>? sel = flame.SelectedFrame;
                FlameNode<AllocationTreeNode>? zoom = flame.ZoomedFrame;
                StyledText st = StyledText.Of("zoom ").Fg(Theme.Current.Muted).Append(zoom?.Label ?? "all").Fg(Theme.Current.Accent);
                if (sel is not null)
                {
                    long bytes = (long)sel.Weight;
                    st = st.Append("      ").Append(sel.Label).Fg(Theme.Current.Foreground)
                        .Append($"   {ByteFormat.Human(bytes)}").Fg(Theme.Current.Success)
                        .Append($"   {100.0 * bytes / Math.Max(1, total):0.0}% of total").Fg(Theme.Current.Muted);
                    if (sel.Value.AllocCount > 0)
                    {
                        st = st.Append($"   ·   {sel.Value.AllocCount:N0} objects").Fg(Theme.Current.Muted);
                    }
                }
                info.Content = st;
            }
            flame.OnSelect = _ => ShowInfo();
            flame.OnZoom = _ => ShowInfo();
            ShowInfo();

            return new Stack(Direction.Vertical)
                .Add(new Padding(info, new Thickness(1, 0)), Constraint.Length(1))
                .Add(new Panel(new Padding(flame, new Thickness(1, 0)), title) { BorderStyle = BorderStyle.Rounded }, Constraint.Fill())
                .Add(new Label(new StyledText("  " + hint, Theme.Current.MutedStyle)), Constraint.Length(1));
        }

        // Level 3: instances of a type.
        Widget InstancesPage(Snapshot snap, string typeName)
        {
            InstanceListing listing = snap.Instances(typeName, 200);
            Table t = MakeTable(("Address", Constraint.Length(16), false), ("Type", Constraint.Fill(2), false),
                ("Size", Constraint.Length(10), true), ("Preview", Constraint.Fill(3), false));
            foreach (ObjectInstance inst in listing.Instances)
            {
                t.Rows.Add([$"0x{inst.Address:x}", Short(inst.TypeName), ByteFormat.Human((long)inst.Size), inst.Preview ?? ""]);
            }
            t.OnActivate = i => Follow(snap, new ObjTarget(ParseHex(t.Rows[i][0])));
            return Hinted(new Panel(t, $" {Short(typeName)} — {listing.TotalMatched:N0} instances, {ByteFormat.Human((long)listing.TotalMatchedSize)} ") { BorderStyle = BorderStyle.Rounded },
                "Enter inspect  ·  Backspace back");
        }

        // Level 4: one instance — Inspect (fields, with clickable object references), GC roots, whoalloc.
        // `tab` picks the panel a link wanted to land on.
        Widget InstancePage(Snapshot snap, ulong address, string tab = "Inspect")
        {
            ObjectDetail detail = snap.Inspect(address);

            // Inspect (fields) is cheap and the default, built up front. GC roots (a graph search for
            // holder paths) and whoalloc build lazily when their tab is shown.
            Widget BuildInspect()
            {
                var inspect = new TreeView<string> { RenderLabel = FieldLabel, ShowGuides = true };
                inspect.OnLinkClick = p => Follow(snap, p);
                TreeNode<string> obj = inspect.AddRoot($"{Short(detail.TypeName)} @ 0x{address:x}  ({ByteFormat.Human((long)detail.Size)})");
                if (detail.StringValue is { } str)
                {
                    obj.AddChild($"= \"{str}\"");
                }
                else if (detail.IsArray) { obj.AddChild($"length : int = {detail.ElementCount}"); foreach (string el in detail.Elements.Take(64)) obj.AddChild($"[] = {el}"); }
                else
                {
                    foreach (FieldValue f in detail.Fields) obj.AddChild($"{f.Name} : {Short(f.TypeName)} = {f.Value}");
                }

                obj.ExpandAll();
                inspect.MarkDirty();
                return new Panel(inspect, " Object — click a reference to follow it ") { BorderStyle = BorderStyle.Rounded };
            }

            Widget BuildRoots(CancellationToken ct)
            {
                var roots = new TreeView<RootRow> { RenderLabel = RootLabel, ShowGuides = true };
                roots.OnLinkClick = p => Follow(snap, p);
                IReadOnlyList<GcRootPath> paths = snap.Roots(address, ct);
                if (paths.Count == 0)
                {
                    roots.AddRoot(new RootRow("(not reachable from any GC root — collectable)", null));
                }

                foreach (GcRootPath path in paths)
                {
                    TreeNode<RootRow> root = roots.AddRoot(new RootRow($"{path.Root.Kind} @ 0x{path.Root.Address:x12}", null));
                    TreeNode<RootRow> node = root;
                    foreach (GcRootNode step in path.Path) node = node.AddChild(new RootRow(Short(step.TypeName), step.Address));
                    root.ExpandAll();
                }
                roots.MarkDirty();
                return new Panel(roots, " Why it's alive — click a holder to inspect it ") { BorderStyle = BorderStyle.Rounded };
            }

            var tabs = new Tabs()
                .Add("Inspect", BuildInspect())
                .Add("GC roots", () => LensCt(BuildRoots));

            if (snap.HasCorrelation && snap.WhoAllocated(address) is { } stack)
            {
                // A single object's allocation stack is one path, not a graph, so a flat list reads like
                // the stack trace it is. Site on top (leaf), callers below. Enter a frame → everything
                // allocated through it (that aggregate is where the branching lives).
                tabs.Add("whoalloc", () =>
                {
                    string[] frames = stack.Split(';'); // root -> leaf
                    Table alloc = MakeTable(("Method", Constraint.Fill(2), false), ("Namespace", Constraint.Fill(3), false));
                    var full = new List<string>();
                    for (int i = frames.Length - 1; i >= 0; i--) // leaf (site) first
                    {
                        string f = frames[i];
                        full.Add(f);
                        int dot = f.LastIndexOf('.');
                        alloc.Rows.Add([dot >= 0 ? f[(dot + 1)..] : f, dot >= 0 ? f[..dot] : ""]);
                    }
                    alloc.Sortable = false; // preserve stack order
                    alloc.OnActivate = i => Follow(snap, new MethodTarget(full[i]));
                    return Hinted(new Panel(alloc, " Allocation stack — Enter a frame for its callers ") { BorderStyle = BorderStyle.Rounded },
                        "Enter → callers of this frame  ·  Backspace back");
                });
            }

            tabs.ActiveIndex = tab switch
            {
                "Roots" => 1,
                "whoalloc" => tabs.Items.Count - 1,
                _ => 0,
            };
            return Hinted(tabs, "Tab / ←→ switch view  ·  click a link to drill  ·  Backspace back");
        }

        // Clicking a method lands here: what it allocates (by type), then who calls it (back-traces).
        // Lead with the type breakdown.
        Widget MethodPage(Snapshot snap, string method)
        {
            if (snap.Allocations is not { } profile)
            {
                return Hinted(new Panel(new Padding(new Label(new StyledText("No allocation profile in this snapshot.", Theme.Current.MutedStyle)), new Thickness(1)), " Method ") { BorderStyle = BorderStyle.Rounded }, "Backspace back");
            }
            AllocationProfile through = profile.Through(method);
            if (through.Sites.Count == 0)
            {
                return Hinted(new Panel(new Padding(new Label(new StyledText($"Nothing allocates through {Short(method)}.", Theme.Current.MutedStyle)), new Thickness(1)), " Method ") { BorderStyle = BorderStyle.Rounded }, "Backspace back");
            }

            long inclusive = through.TotalAllocBytes;
            long survived = through.TotalSurvivedBytes;
            long count = through.Sites.Sum(s => s.AllocCount);
            int types = through.ByType().Count;
            StyledText summary = StyledText.Of(Short(method)).Bold().Fg(Theme.Current.Accent)
                .Append($"   {ByteFormat.Human(inclusive)} allocated").Fg(Theme.Current.Success)
                .Append($"   {ByteFormat.Human(survived)} survived").Fg(Theme.Current.Foreground)
                .Append($"   {count:N0} objects").Fg(Theme.Current.Muted)
                .Append($"   ·   {through.Sites.Count:N0} call paths").Fg(Theme.Current.Muted);
            if (through.HasTypes)
            {
                summary = summary.Append($"   ·   {types} types").Fg(Theme.Current.Muted);
            }

            var tabs = new Tabs();
            if (through.HasTypes)
            {
                tabs.Add("Allocated types", () => Hinted(new Panel(TypeTable(snap, through, name => new TypeTarget(name)), " What this method allocates, by type ") { BorderStyle = BorderStyle.Rounded },
                    "Enter → live instances of this type  ·  s sort  ·  Backspace back"));
            }
            // A back-trace is a focused drill, a tree-table with numbers, not a flamegraph. Built lazily
            // (BuildCallers folds the whole profile) only when the Callers tab is shown.
            tabs.Add("Callers", () =>
            {
                AllocationTreeNode callers = AllocationTreeNode.BuildCallers(profile, method);
                return callers.Children.Count > 0
                    ? Hinted(new Panel(AllocTreeTable(snap, callers.Children, callers.AllocBytes), " Who calls it — bytes allocated through each caller ") { BorderStyle = BorderStyle.Rounded },
                        "→/← expand · collapse   ·   click a frame → its stats   ·   Backspace back")
                    : new Panel(new Padding(new Label(new StyledText("This is a top-level frame — no callers.", Theme.Current.MutedStyle)), new Thickness(1)), " Callers ") { BorderStyle = BorderStyle.Rounded };
            });

            return new Stack(Direction.Vertical)
                .Add(new Padding(new Label(summary), new Thickness(1, 0)), Constraint.Length(1))
                .Add(tabs, Constraint.Fill());
        }

        FlameNode<AllocationTreeNode> ToFlame(AllocationTreeNode node, string? label = null)
        {
            var frame = new FlameNode<AllocationTreeNode>(node, label ?? Short(node.Frame), Math.Max(1, node.AllocBytes));
            foreach (AllocationTreeNode child in node.Children) frame.Add(ToFlame(child));
            return frame;
        }

        Table HotTable(AllocationProfile profile, Action<string> onPick)
        {
            var self = new Dictionary<string, (long Bytes, long Count)>(StringComparer.Ordinal);
            var inclusive = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (AllocationSite site in profile.Sites)
            {
                if (site.Frames.Count == 0)
                {
                    continue;
                }

                string leaf = site.Frames[^1];
                (long Bytes, long Count) cur = self.GetValueOrDefault(leaf);
                self[leaf] = (cur.Bytes + site.AllocBytes, cur.Count + site.AllocCount);
                foreach (string frame in site.Frames.Distinct()) inclusive[frame] = inclusive.GetValueOrDefault(frame) + site.AllocBytes;
            }
            Table t = MakeTable(("Self", Constraint.Length(11), true), ("Inclusive", Constraint.Length(11), true),
                ("Count", Constraint.Length(9), true), ("Method", Constraint.Fill(3), false));
            var methods = new List<string>();
            foreach ((string method, (long bytes, long count)) in self.OrderByDescending(kv => kv.Value.Bytes).Take(200))
            {
                methods.Add(method);
                t.Rows.Add([ByteFormat.Human(bytes), ByteFormat.Human(inclusive.GetValueOrDefault(method)), count.ToString("N0", CultureInfo.InvariantCulture), Short(method)]);
            }
            t.OnActivate = i => onPick(methods[i]);
            return t;
        }

        // Level 1 (root): the snapshot list.
        Table snaps = MakeTable(("Id", Constraint.Length(5), false), ("Process", Constraint.Fill(2), false),
            ("When", Constraint.Length(14), false), ("Size", Constraint.Length(10), true), ("Captured", Constraint.Fill(2), false));
        foreach ((ProcessRecord process, SnapshotEntry snap) in entries)
        {
            string kind = snap.HasCorrelation
                ? "heap + alloc + corr"
                : snap.HasAllocations ? "heap + alloc" : "heap only";
            snaps.Rows.Add([snap.Id, process.Name ?? "?", snap.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm"), ByteFormat.Human(snap.TotalSizeBytes), snap.Reason is { } r ? $"{kind} ({r})" : kind]);
        }
        snaps.OnActivate = i =>
        {
            string id = snaps.Rows[i][0];
            current?.Dispose();
            current = store.Open(id);
            nav.Push(new Page(id, Workspace(current, id)));
        };

        nav.Reset(new Page("Snapshots", Hinted(new Panel(snaps, " Snapshots ") { BorderStyle = BorderStyle.Rounded },
            "Enter to open a snapshot  ·  q to quit")));

        using var terminal = new AnsiTerminal();
        using var app = new App(terminal);

        app.Root = new Stack(Direction.Vertical)
            .Add(nav, Constraint.Fill())
            .Add(new Label(new StyledText(" ↑/↓ move  ·  Enter / click drill in  ·  Tab switch lens  ·  Backspace back  ·  q quit", Theme.Current.MutedStyle)), Constraint.Length(1));

        app.OnEvent = e =>
        {
            if (e is KeyEvent { IsChar: true } k && k.Rune.Value == 'q')
            {
                app.Quit();
                return true;
            }
            return nav.OnEvent(e);
        };

        await app.RunAsync();
        current?.Dispose();
        return 0;

        // ---- helpers ----

        static Table MakeTable(params (string Header, Constraint Width, bool Right)[] columns)
        {
            var t = new Table { ShowHeader = true, SelectedIndex = 0, Striped = true, ShowScrollbar = true, Sortable = true };
            foreach ((string header, Constraint width, bool right) in columns)
            {
                t.Columns.Add(new Column(header, width, right ? Justify.Right : Justify.Left) { SortKey = right ? s => ParseNum(s) : null });
            }
            return t;
        }

        static Widget Hinted(Widget body, string hint) =>
            new Stack(Direction.Vertical)
                .Add(body, Constraint.Fill())
                .Add(new Label(new StyledText("  " + hint, Theme.Current.MutedStyle)), Constraint.Length(1));

        static string Bar(double pct, int width)
        {
            int filled = (int)Math.Round(Math.Clamp(pct, 0, 100) / 100.0 * width);
            return new string('█', filled) + new string('░', width - filled);
        }

        // A field row `name : type = value`, with an object-reference value rendered as a clickable link.
        static StyledText FieldLabel(TreeNode<string> node)
        {
            Theme theme = Theme.Current;
            string text = node.Value;
            int colon = text.IndexOf(" : ", StringComparison.Ordinal);
            if (colon < 0)
            {
                int e0 = text.IndexOf(" = ", StringComparison.Ordinal);
                if (e0 >= 0 && TryAddr(text[(e0 + 3)..], out ulong a0))
                {
                    return StyledText.Of(text[..(e0 + 3)]).Fg(theme.Foreground).Append(text[(e0 + 3)..]).Fg(theme.Accent).Underline().Link(new ObjTarget(a0));
                }

                return new StyledText(text, new Style(theme.Foreground, Color.Default));
            }
            StyledText st = StyledText.Of(text[..colon]).Fg(theme.Foreground).Append(" : ").Fg(theme.Muted);
            string rest = text[(colon + 3)..];
            int eq = rest.IndexOf(" = ", StringComparison.Ordinal);
            if (eq < 0)
            {
                return st.Append(rest).Fg(theme.Secondary);
            }

            st.Append(rest[..eq]).Fg(theme.Secondary).Append(" = ").Fg(theme.Muted);
            string value = rest[(eq + 3)..];
            return TryAddr(value, out ulong a)
                ? st.Append(value).Fg(theme.Accent).Underline().Link(new ObjTarget(a))
                : st.Append(value).Fg(theme.Foreground);
        }

        // A GC-root path node: the root handle (plain), or a held object `Type @0xADDR` with the
        // address linked so you can walk up the chain of holders.
        static StyledText RootLabel(TreeNode<RootRow> node)
        {
            RootRow r = node.Value;
            return r.Address is ulong a
                ? StyledText.Of(r.Text + " ").Fg(Theme.Current.Foreground).Append($"@0x{a:x}").Fg(Theme.Current.Accent).Underline().Link(new ObjTarget(a))
                : new StyledText(r.Text, new Style(Theme.Current.Muted, Color.Default));
        }

        static bool TryAddr(string s, out ulong addr)
        {
            addr = 0;
            s = s.Trim();
            return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && ulong.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr)
                && addr != 0;
        }

        static ulong ParseHex(string s) =>
            ulong.TryParse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong v) ? v : 0;

        static double ParseNum(string s)
        {
            s = s.Trim();
            double mult = 1;
            foreach ((string suffix, double m) in new[] { ("TB", 1024d * 1024 * 1024 * 1024), ("GB", 1024d * 1024 * 1024), ("MB", 1024d * 1024), ("KB", 1024d), ("B", 1d) })
            {
                if (s.EndsWith(suffix, StringComparison.Ordinal)) { mult = m; s = s[..^suffix.Length].Trim(); break; }
            }
            return double.TryParse(s.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out double value) ? value * mult : 0;
        }

        static string Short(string type)
        {
            int generic = type.IndexOf('<');
            string head = generic >= 0 ? type[..generic] : type;
            int dot = head.LastIndexOf('.');
            return dot >= 0 ? type[(dot + 1)..] : type;
        }
    }
}

file sealed record ObjTarget(ulong Address, string Tab = "Inspect");
file sealed record TypeTarget(string Type, string Tab = "Instances"); // → that type's detail (Instances / Call tree)
file sealed record MethodTarget(string Method);
file sealed record RootRow(string Text, ulong? Address);

// A filter prompt above a table: `/` focuses the input, Esc returns focus to the table. The table
// is focused by default so ↑/↓/Enter drive the list without a detour through the prompt.
file sealed class FilterStack : Widget
{
    private readonly Input _input;
    private readonly Widget _body;
    private readonly Stack _stack;

    public FilterStack(Input input, Widget body)
    {
        _input = input;
        _body = body;
        _stack = new Stack(Direction.Vertical)
            .Add(input, Constraint.Length(1))
            .Add(body, Constraint.Fill());
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

    public override bool OnEvent(InputEvent e)
    {
        if (e is KeyEvent key)
        {
            // `/` opens the filter (unless the input already has it, so you can type a literal slash).
            if (!_input.HasFocus && key.IsChar && key.Rune.Value == '/')
            {
                _body.HasFocus = false;
                _input.HasFocus = true;
                return true;
            }
            // Esc leaves the filter and hands the arrows/Enter back to the table.
            if (_input.HasFocus && key.Key == Key.Escape)
            {
                _input.HasFocus = false;
                _body.HasFocus = true;
                return true;
            }
        }
        return _stack.OnEvent(e);
    }
}
