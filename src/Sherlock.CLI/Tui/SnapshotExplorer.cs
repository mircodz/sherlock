using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Terminal;
using Cellar.Theming;
using Cellar.Widgets;
using Sherlock.Core;
using Sherlock.Core.Profiling;
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

        Widget Workspace(Snapshot snapshot, SnapshotEntry entry)
        {
            void Navigate(NavigationTarget target) => Follow(snapshot, target);
            Tabs tabs = new Tabs()
                .AddBackgroundView("Health", _ => HealthView.Create(Overview(snapshot, entry)))
                .AddBackgroundView("Types", _ => TypesView.Create(snapshot.Histogram, Navigate))
                .AddBackgroundView("Retention", cancellation => RetentionView.Create(snapshot.GetDominatorTree(cancellation), Navigate));
            if (!entry.HasAllocations)
            {
                return tabs.AddView("Allocations", () => AllocationsView.MissingProfile(" Allocations "));
            }
            return tabs
                .AddBackgroundView("Allocations", _ => AllocationsView.ByType(snapshot.Allocations, Navigate))
                .AddBackgroundView("Call tree", _ => AllocationsView.Flow(snapshot.Allocations))
                .AddBackgroundView("Hot methods", _ => AllocationsView.Hot(snapshot.Allocations, Navigate));
        }

        bool labelled = entries.Any(entry => entry.Snapshot.Label is not null);
        var columns = new List<(string Header, Constraint Width, bool Right)>
        {
            ("Id", Constraint.Length(5), false), ("App", Constraint.Fill(2), false), ("When", Constraint.Length(12), false),
        };
        if (labelled)
        {
            columns.Add(("Label", Constraint.Fill(1), false));
        }
        columns.AddRange([("On disk", Constraint.Length(10), true), ("Contents", Constraint.Fill(2), false)]);
        Table table = Table([.. columns]);
        SetRows(table, entries, entry =>
        {
            SnapshotEntry snapshot = entry.Snapshot;
            string contents = snapshot.HasCorrelation ? "heap + allocations + whoalloc" : snapshot.HasAllocations ? "heap + allocations" : "heap";
            var row = new List<string> { snapshot.Id, $"{entry.Process.Name ?? "?"} ({entry.Process.Pid})", snapshot.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm") };
            if (labelled)
            {
                row.Add(snapshot.Label ?? "");
            }
            row.AddRange([ByteFormat.Column(snapshot.TotalSizeBytes), snapshot.Reason is { } reason ? $"{contents} \u00b7 on {reason}" : contents]);
            return [.. row];
        }, entry =>
        {
            Snapshot opened = store.Open(entry.Snapshot.Id);
            current?.Dispose();
            current = opened;
            navigation.Push(new Page(entry.Snapshot.Id, Workspace(opened, entry.Snapshot)));
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

    private static HeapOverview Overview(Snapshot snapshot, SnapshotEntry entry)
    {
        DumpInfo info = snapshot.Info;
        string gc = info.ServerGc ? $"server GC, {info.HeapCount} heaps" : "workstation GC";
        string captured = entry.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm") + (entry.Reason is { } reason ? $" \u00b7 on {reason}" : "");
        return new HeapOverview(entry.Id, snapshot.Histogram, snapshot.Generations, $"{info.ClrFlavor} {info.ClrVersion} \u00b7 {gc}", captured);
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

    private static Widget TypePage(Snapshot snapshot, TypeTarget target, Action<NavigationTarget> navigate)
    {
        Tabs tabs = new Tabs()
            .AddBackgroundView("Instances", cancellation =>
                TypesView.Instances(snapshot.Instances(target.Type, 200, cancellation, exact: true), target.Type, navigate))
            .AddBackgroundView("Call tree", _ => AllocationsView.ForType(snapshot.Allocations, target.Type, navigate));
        tabs.ActiveIndex = target.Tab == TypeTab.Allocations ? 1 : 0;
        return tabs;
    }
}
