using System;
using System.Linq;
using System.Threading.Tasks;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Terminal;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Sherlock.Core;
using Sherlock.Core.Profiling;
using Sherlock.Core.Diagnostics;
using Sherlock.Core.Store;
using static Sherlock.CLI.Tui.ViewFormatting;

namespace Sherlock.CLI.Tui;

/// <summary>Owns the active snapshot and routes navigation between focused explorer views.</summary>
public static class SnapshotExplorer
{
    public static async Task<int> Run()
    {
        Rendering.Theme.ApplyCellar();
        var store = SnapshotStore.Default();
        var entries = store.Sessions
            .SelectMany(session => session.Processes.SelectMany(process => process.Snapshots.Select(snapshot => (Process: process, Snapshot: snapshot))))
            .Where(entry => entry.Snapshot.Exists)
            .OrderByDescending(entry => entry.Snapshot.CreatedAt)
            .ToList();
        if (entries.Count == 0)
        {
            Console.Error.WriteLine("No snapshots in the library. Capture one with `sl collect` or `sl run` first.");
            return 1;
        }

        // Keys go to the focused view first; see RouteKey.
        var navigation = new Navigator { BackKey = Key.None };
        Snapshot? current = null;

        void Follow(Snapshot snapshot, NavigationTarget target)
        {
            void Navigate(NavigationTarget next) => Follow(snapshot, next);
            Page page = target switch
            {
                ObjTarget obj => new Page($"0x{obj.Address:x}", ObjectView.Create(snapshot, obj.Address, obj.Tab, Navigate)),
                TypeTarget type => new Page(TypeNames.Short(type.Type), TypePage(snapshot, type, Navigate)),
                MethodTarget method => new Page(FrameNames.ShortMethod(method.Method), AllocationsView.ForMethod(snapshot.Allocations, method.Method, Navigate)),
                _ => throw new ArgumentException("Unknown explorer navigation target.", nameof(target)),
            };
            navigation.Push(page);
        }

        Widget Workspace(Snapshot snapshot, string id)
        {
            void Navigate(NavigationTarget target) => Follow(snapshot, target);
            return new Tabs()
                .Add("Health", () => Lazy(() => HealthView.Create(snapshot.Histogram,
                    HeapDoctor.QuickFindings(snapshot.Histogram, snapshot.Dominators), id, Navigate)))
                .Add("Types", () => Lazy(() => TypesView.Create(snapshot.Histogram, Navigate)))
                .Add("Retention", () => Lazy(() => RetentionView.Create(snapshot.Dominators, Navigate)))
                .Add("Allocations", () => Lazy(() => AllocationsView.Create(snapshot.Allocations, Navigate)));
        }

        Table table = Table(("Id", Constraint.Length(5), false), ("Process", Constraint.Fill(2), false),
            ("When", Constraint.Length(14), false), ("Size", Constraint.Length(10), true), ("Captured", Constraint.Fill(2), false));
        SetRows(table, entries, entry =>
        {
            SnapshotEntry snapshot = entry.Snapshot;
            string contents = snapshot.HasCorrelation ? "heap + alloc + corr" : snapshot.HasAllocations ? "heap + alloc" : "heap only";
            return [snapshot.Id, entry.Process.Name ?? "?", snapshot.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm"),
                ByteFormat.Human(snapshot.TotalSizeBytes), snapshot.Reason is { } reason ? $"{contents} ({reason})" : contents];
        }, entry =>
        {
            Snapshot opened = store.Open(entry.Snapshot.Id);
            current?.Dispose();
            current = opened;
            navigation.Push(new Page(entry.Snapshot.Id, Workspace(opened, entry.Snapshot.Id)));
        });
        navigation.Reset(new Page("Snapshots", Hinted(new Panel(table, " Snapshots ") { BorderStyle = BorderStyle.Rounded },
            "\u2191\u2193 move  \u00b7  Enter open", tabbed: false, canGoBack: false)));

        using var terminal = new AnsiTerminal();
        using var app = new App(terminal);
        app.Root = navigation;
        app.OnEvent = input => RouteKey(navigation, input, app.Quit);
        try
        {
            await app.RunAsync();
            return 0;
        }
        finally
        {
            current?.Dispose();
        }
    }

    /// <summary>The focused view sees every key first, so the filter box can type 'q' and delete with Backspace;
    /// only keys it leaves unhandled go back (Backspace) or quit (q).</summary>
    internal static bool RouteKey(Navigator navigation, InputEvent input, Action quit)
    {
        if (navigation.OnEvent(input))
        {
            return true;
        }
        if (input is KeyEvent { Key: Key.Backspace } && navigation.Depth > 1)
        {
            navigation.Pop();
            return true;
        }
        if (input is KeyEvent { IsChar: true } key && key.Rune.Value == 'q')
        {
            quit();
            return true;
        }
        return false;
    }

    private static Widget Lazy(Func<Widget> build) => new LazyContent(_ => build());

    private static Widget TypePage(Snapshot snapshot, TypeTarget target, Action<NavigationTarget> navigate)
    {
        var tabs = new Tabs()
            .Add("Instances", () => new LazyContent(_ => TypesView.Instances(snapshot.Instances(target.Type, 200, exact: true), target.Type, navigate)))
            .Add("Call tree", () => new LazyContent(_ => AllocationsView.ForType(snapshot.Allocations, target.Type, navigate)));
        tabs.ActiveIndex = target.Tab == TypeTab.Allocations ? 1 : 0;
        return tabs;
    }
}
