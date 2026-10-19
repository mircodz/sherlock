using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Terminal;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;
using Sherlock.Core;
using Sherlock.Core.Store;
using static Sherlock.CLI.Tui.ViewFormatting;

namespace Sherlock.CLI.Tui;

/// <summary>The snapshot library, and the pages of the snapshot opened from it.</summary>
public static class SnapshotExplorer
{
    /// <summary>Goes back a page. A view sees it first, so a filter box can still delete with it.</summary>
    internal const Key BackKey = Key.Backspace;

    /// <summary>Quits, unless the view used the key: a filter box can type a q.</summary>
    internal static bool IsQuitKey(InputEvent input) => input is KeyEvent { IsChar: true } key && key.Rune.Value == 'q';

    public static async Task<int> Run()
    {
        Rendering.Theme.ApplyCellar();
        var store = SnapshotStore.Default();
        List<LibraryEntry> entries = store.Sessions
            .SelectMany(session => session.Processes.SelectMany(process => process.Snapshots.Select(snapshot => new LibraryEntry(process, snapshot))))
            .Where(entry => entry.Snapshot.Exists)
            .OrderByDescending(entry => entry.Snapshot.CreatedAt)
            .ToList();
        if (entries.Count == 0)
        {
            Console.Error.WriteLine("No snapshots in the library. Capture one with `sl collect` or `sl run` first.");
            return 1;
        }

        var navigation = new Navigator { BackKey = BackKey };
        OpenedSnapshot? current = null;

        void Open(SnapshotEntry entry)
        {
            // Opening the snapshot that's already open keeps what it has analyzed.
            if (current?.Entry.Id != entry.Id)
            {
                Snapshot snapshot = store.Open(entry.Id);
                current?.Close();
                current = new OpenedSnapshot(snapshot, entry);
            }
            OpenedSnapshot opened = current;
            void Navigate(NavigationTarget target) => navigation.Push(Page(opened, target, Navigate));
            navigation.Push(new Page(entry.Id, Workspace(opened, Navigate)));
        }

        Table<LibraryEntry> table = Table(entries, entry => Open(entry.Snapshot), LibraryColumns(entries).ToArray());
        navigation.Reset(new Page("Snapshots", View("Snapshots", table, "\u2191\u2193 move  \u00b7  Enter open", tabbed: false, canGoBack: false)));

        using var terminal = new AnsiTerminal();
        using var app = new App(terminal);
        app.Root = navigation;
        app.OnUnhandledEvent = input =>
        {
            if (!IsQuitKey(input))
            {
                return false;
            }
            app.Quit();
            return true;
        };
        app.OnError = error =>
        {
            ShowError(app, error);
            return true;
        };
        try
        {
            await app.RunAsync();
            return 0;
        }
        finally
        {
            current?.Close();
        }
    }

    private static Page Page(OpenedSnapshot opened, NavigationTarget target, Action<NavigationTarget> navigate) => target switch
    {
        ObjTarget obj => new Page(Rendering.Addresses.Format(obj.Address), ObjectView.Create(opened, obj.Address, obj.Tab, navigate)),
        TypeTarget type => new Page(Rendering.DisplayNames.Type(type.Type), TypePage(opened, type, navigate)),
        MethodTarget method => new Page(method.Method.Label, AllocationsView.ForMethod(opened.Snapshot.Allocations, method.Method, navigate)),
        _ => throw new ArgumentException("Unknown explorer navigation target.", nameof(target)),
    };

    private static Widget Workspace(OpenedSnapshot opened, Action<NavigationTarget> navigate)
    {
        SnapshotEntry entry = opened.Entry;
        Tabs tabs = Views()
            .Add("Health", opened.Background((snapshot, cancellation) => HealthView.Create(Overview(snapshot, entry, cancellation))))
            .Add("Types", opened.Background((snapshot, cancellation) => TypesView.Create(snapshot.GetHistogram(cancellation), navigate)))
            .Add("Retention", opened.Background((snapshot, cancellation) => RetentionView.Create(snapshot.GetDominatorTree(cancellation), navigate)));
        if (!entry.HasAllocations)
        {
            return tabs.Add("Allocations", AllocationsView.MissingProfile("Allocations"));
        }
        return tabs
            .Add("Allocations", opened.Background((snapshot, _) => AllocationsView.ByType(snapshot.Allocations, navigate)))
            .Add("Call tree", opened.Background((snapshot, _) => AllocationsView.Flow(snapshot.Allocations)))
            .Add("Hot methods", opened.Background((snapshot, _) => AllocationsView.Hot(snapshot.Allocations, navigate)));
    }

    private static Widget TypePage(OpenedSnapshot opened, TypeTarget target, Action<NavigationTarget> navigate)
    {
        Tabs tabs = Views()
            .Add("Instances", opened.Background((snapshot, cancellation) =>
                TypesView.Instances(snapshot.Instances(target.Type, 200, cancellation, exact: true), target.Type, navigate)))
            .Add("Call tree", opened.Background((snapshot, _) => AllocationsView.ForType(snapshot.Allocations, target.Type, navigate)));
        tabs.ActiveIndex = target.Tab == TypeTab.Allocations ? 1 : 0;
        return tabs;
    }

    private static IEnumerable<TableColumn<LibraryEntry>> LibraryColumns(IReadOnlyList<LibraryEntry> entries)
    {
        yield return TextColumn<LibraryEntry>("Id", Constraint.Length(5), entry => entry.Snapshot.Id, Theme.Current.Secondary);
        yield return new TableColumn<LibraryEntry>("App", Constraint.Fill(2), entry =>
            StyledText.Of(entry.Process.Name is { } name ? Printable.Escape(name) : "?").Fg(Theme.Current.Accent)
                .Append($" ({entry.Process.Pid})").Fg(Theme.Current.Muted));
        yield return new TableColumn<LibraryEntry>("When", Constraint.Length(12),
            entry => new StyledText(When(entry.Snapshot), Theme.Current.MutedStyle))
        {
            SortKey = entry => entry.Snapshot.CreatedAt,
        };
        if (entries.Any(entry => entry.Snapshot.Label is not null))
        {
            yield return TextColumn<LibraryEntry>("Label", Constraint.Fill(1), entry => entry.Snapshot.Label ?? "", Theme.Current.Foreground);
        }
        yield return BytesColumn<LibraryEntry>("On disk", entry => entry.Snapshot.TotalSizeBytes, Theme.Current.Success);
        yield return TextColumn<LibraryEntry>("Contents", Constraint.Fill(2), entry => SnapshotContents(entry.Snapshot), Theme.Current.Muted);
    }

    private static string When(SnapshotEntry snapshot) => snapshot.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static HeapOverview Overview(Snapshot snapshot, SnapshotEntry entry, CancellationToken cancellation)
    {
        DumpInfo info = snapshot.Info;
        string gc = info.ServerGc ? $"server GC, {info.HeapCount} heaps" : "workstation GC";
        string captured = When(entry) + (entry.Reason is { } reason ? $" \u00b7 on {reason}" : "");
        return new HeapOverview(entry.Id, snapshot.GetHistogram(cancellation), snapshot.Generations, $"{info.ClrFlavor} {info.ClrVersion} \u00b7 {gc}", captured);
    }

    /// <summary>Shows what went wrong over the page, such as a snapshot that won't open, instead of ending the explorer.
    /// Esc closes it.</summary>
    private static void ShowError(App app, Exception error)
    {
        bool expected = error is DumpAnalysisException or IOException or InvalidDataException or UnauthorizedAccessException;
        string message = expected ? error.Message : $"{error.GetType().Name}: {error.Message}";
        var text = new Label(StyledText.Of(message).Fg(Theme.Current.Foreground).Append("\n\nEsc to close").Fg(Theme.Current.Muted), wrap: true);
        app.PushOverlay(new Overlay(new Panel(text, " Error "))
        {
            WidthPercent = 60,
            Height = 9,
        });
    }

    private sealed record LibraryEntry(ProcessRecord Process, SnapshotEntry Snapshot);
}
