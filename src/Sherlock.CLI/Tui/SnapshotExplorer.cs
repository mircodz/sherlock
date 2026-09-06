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

        var navigation = new Navigator { BackKey = Key.Backspace };
        Snapshot? current = null;

        void Follow(Snapshot snapshot, NavigationTarget target)
        {
            void Navigate(NavigationTarget next) => Follow(snapshot, next);
            Page page = target switch
            {
                ObjTarget obj => new Page($"0x{obj.Address:x}", ObjectView.Create(snapshot, obj.Address, obj.Tab, Navigate)),
                TypeTarget type => new Page(TypeNames.Short(type.Type), TypePage(snapshot, type, Navigate)),
                MethodTarget method => new Page(TypeNames.Short(method.Method), AllocationsView.ForMethod(snapshot.Allocations, method.Method, Navigate)),
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
            "Enter to open a snapshot  \u00b7  q to quit")));

        using var terminal = new AnsiTerminal();
        using var app = new App(terminal);
        app.Root = new Stack(Direction.Vertical)
            .Add(navigation, Constraint.Fill())
            .Add(new Label(new StyledText(" \u2191/\u2193 move  \u00b7  Enter / click drill in  \u00b7  Tab switch lens  \u00b7  Backspace back  \u00b7  q quit", Theme.Current.MutedStyle)), Constraint.Length(1));
        app.OnEvent = input =>
        {
            if (input is KeyEvent { IsChar: true } key && key.Rune.Value == 'q')
            {
                app.Quit();
                return true;
            }
            return navigation.OnEvent(input);
        };
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

    private static Widget Lazy(Func<Widget> build) => new AsyncContent(_ => build());

    private static Widget TypePage(Snapshot snapshot, TypeTarget target, Action<NavigationTarget> navigate)
    {
        var tabs = new Tabs()
            .Add("Instances", () => TypesView.Instances(snapshot.Instances(target.Type, 200), target.Type, navigate))
            .Add("Call tree", () => AllocationsView.ForType(snapshot.Allocations, target.Type, navigate));
        tabs.ActiveIndex = target.Tab == TypeTab.Allocations ? 1 : 0;
        return Hinted(tabs, "Tab / \u2190\u2192 switch view  \u00b7  Enter drill in  \u00b7  Backspace back");
    }
}
