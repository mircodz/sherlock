using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Collection;
using Sherlock.Core.Store;
using Tessera.Widgets.Charts;
using Tessera.Layout;
using Tessera.Primitives;
using Tessera.Terminal;
using Tessera.Text;
using Tessera.Widgets;
using Tessera.Widgets.Charts.Trees;
using Theme = Tessera.Theming.Theme;

namespace Sherlock.CLI.Live;

/// <summary>
/// The <c>run --live</c> dashboard: a Tessera TUI hosted in-process over a running supervised
/// target. Shows the live managed-heap size (polled over the profiler control channel) and the
/// process tree; Enter snapshots the selected process into the library on demand, k kills the tree.
/// The captured snapshots stay in the store, so you analyze them (here, or later via <c>sl</c>).
/// </summary>
public static class LiveDashboard
{
    // Background samples posted onto the UI loop.
    private sealed record HeapSample(HeapStats? Heap, long Gc);
    private sealed record ProcessList(IReadOnlyList<SupervisedProcess> Processes);
    private sealed record Capturing(int Pid, string Name);
    private sealed record Captured(string Id, long Bytes, string Reason);
    private sealed record Status(string Text, Color Color);

    public static void Run(Workspace workspace, ProcessSupervisor supervisor, RunSpec spec, CancellationToken cancellation)
    {
        var poll = TimeSpan.FromMilliseconds(200); // control-channel request timeout
        var busy = 0; // 1 while a capture is in flight — pause heap polls so we don't contend on the channel

        // --- widgets ---
        var heap = new Sparkline { BaselineZero = true, Color = Theme.Current.Accent };
        var stat = new Label(StyledText.Empty());

        // The live process tree. Rows are links (click a process to snapshot it); Enter snapshots
        // the selected one. Children come from the ParentPid links, filtered against the live set.
        var procs = new List<SupervisedProcess>();
        var procTree = new TreeView<SupervisedProcess>
        {
            RenderLabel = n =>
            {
                SupervisedProcess p = n.Value;
                Color c = p.IsDotnet ? Theme.Current.Accent : Theme.Current.Muted;
                return StyledText.Of(p.Name).Fg(c).Underline().Link(p.Pid)
                    .Append($"  pid {p.Pid}").Fg(Theme.Current.Muted)
                    .Append(p.IsRoot ? "  root" : "").Fg(Theme.Current.Muted);
            },
            ShowGuides = true,
            ShowHeader = true,
        };
        procTree.Columns.Add(new TreeColumn<SupervisedProcess>(".NET", 7, n =>
            new StyledText(n.Value.IsDotnet ? "yes" : "—", Theme.Current.MutedStyle)));

        var snapTable = new Table { ShowHeader = true, Striped = true, ShowScrollbar = true };
        snapTable.Columns.Add(new Column("Snapshot", Constraint.Length(10)));
        snapTable.Columns.Add(new Column("Size", Constraint.Length(10), Justify.Right));
        snapTable.Columns.Add(new Column("Captured", Constraint.Fill(2)));

        string title = string.Join(' ', spec.Command);

        var left = new Stack(Direction.Vertical)
            .Add(new Panel(procTree, " Processes — click or Enter to snapshot ") { BorderStyle = BorderStyle.Rounded }, Constraint.Fill(1))
            .Add(new Panel(snapTable, " Snapshots captured ") { BorderStyle = BorderStyle.Rounded }, Constraint.Fill(1));

        var right = new Stack(Direction.Vertical)
            .Add(new Padding(stat, new Thickness(1, 0)), Constraint.Length(2))
            .Add(new Panel(new Padding(heap, new Thickness(1, 0)), " Managed heap ") { BorderStyle = BorderStyle.Rounded }, Constraint.Fill());

        var body = new Stack(Direction.Horizontal)
            .Add(left, Constraint.Fill(2))
            .Add(right, Constraint.Fill(3));

        using var terminal = new AnsiTerminal();
        using var app = new App(terminal);

        void Snapshot(int pid)
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return; // one capture at a time
            string name = procs.FirstOrDefault(p => p.Pid == pid)?.Name ?? "process";
            app.Post(new Capturing(pid, name));
            _ = Task.Run(() =>
            {
                try
                {
                    CaptureResult r = workspace.Capture(pid, load: false);
                    app.Post(new Captured(r.Entry.Id, r.Entry.SizeBytes, r.Entry.Reason ?? "heap"));
                }
                catch (Exception ex)
                {
                    app.Post(new Status($"✗ capture failed: {ex.Message}", Theme.Current.Error));
                }
                finally
                {
                    Interlocked.Exchange(ref busy, 0);
                }
            });
        }

        void SnapshotSelected()
        {
            if (procTree.SelectedNode is { } n)
            {
                Snapshot(n.Value.Pid);
            }
        }

        procTree.OnLinkClick = p => Snapshot((int)p);      // click a process row → snapshot it
        procTree.OnActivate = n => Snapshot(n.Value.Pid);  // Enter on the selected row

        var footer = new Stack(Direction.Horizontal)
            .Add(FooterButton("↵", "Snapshot", SnapshotSelected), Constraint.Length(13))
            .Add(FooterButton("k", "Kill", () => { supervisor.Kill(); app.Post(new Status("killed the process tree.", Theme.Current.Warning)); }), Constraint.Length(9))
            .Add(FooterButton("q", "Quit", app.Quit), Constraint.Length(9));

        // A prominent status banner — capture is a blocking, second-or-two operation, so we make it
        // unmistakable (it repaints immediately, not on the next heap tick).
        var banner = new Label(new StyledText("  ↑/↓ pick a process, then click it or press Enter to capture a heap snapshot.", Theme.Current.MutedStyle));

        app.Root = new Stack(Direction.Vertical)
            .Add(new Padding(new Label(StyledText.Of("sl live  ").Bold().Fg(Theme.Current.Accent).Append(title).Fg(Theme.Current.Muted)), new Thickness(1, 0)), Constraint.Length(1))
            .Add(new Padding(banner, new Thickness(1, 0)), Constraint.Length(1))
            .Add(body, Constraint.Fill())
            .Add(footer, Constraint.Length(1));

        app.OnEvent = e =>
        {
            switch (e)
            {
                case KeyEvent { IsChar: true } k when k.Rune.Value == 'q':
                    app.Quit();
                    return true;
                case KeyEvent { IsChar: true } k when k.Rune.Value == 'k':
                    supervisor.Kill();
                    app.Post(new Status("killed the process tree.", Theme.Current.Warning));
                    return true;
            }
            return procTree.OnEvent(e); // arrows navigate; Enter fires OnActivate; clicks fire OnLinkClick
        };

        var pidSet = new HashSet<int>();
        app.OnMessage = msg =>
        {
            switch (msg)
            {
                case HeapSample s:
                    if (s.Heap is { } h)
                    {
                        heap.Push(h.Total);
                        stat.Content = StyledText.Of(ByteFormat.Human(h.Total)).Bold().Fg(Theme.Current.Accent)
                            .Append("  managed heap").Fg(Theme.Current.Muted)
                            .Append($"    gen0 {ByteFormat.Human(h.Gen0)} · gen1 {ByteFormat.Human(h.Gen1)} · gen2 {ByteFormat.Human(h.Gen2)} · LOH {ByteFormat.Human(h.Loh)}").Fg(Theme.Current.Muted)
                            .Append($"    {s.Gc} GCs").Fg(Theme.Current.Muted);
                    }
                    break;
                case Capturing c:
                    banner.Content = StyledText.Of("  ⏳ Capturing heap snapshot of ").Bold().Fg(Theme.Current.Warning)
                        .Append($"{c.Name} (pid {c.Pid})").Fg(Theme.Current.Foreground)
                        .Append(" — this pauses the live view for a moment…").Fg(Theme.Current.Muted);
                    app.Invalidate(); // repaint now, don't wait for the next poll tick
                    break;
                case Status st:
                    banner.Content = new StyledText("  " + st.Text, new Style(st.Color, Color.Default));
                    app.Invalidate();
                    break;
                case ProcessList pl:
                    procs.Clear();
                    procs.AddRange(pl.Processes);
                    // Rebuild only when the process set changes — otherwise we'd flicker and reset the
                    // selection every poll. Roots are IsRoot (or any whose parent isn't in the set).
                    HashSet<int> now = pl.Processes.Select(p => p.Pid).ToHashSet();
                    if (!now.SetEquals(pidSet))
                    {
                        pidSet = now;
                        procTree.Clear();
                        foreach (SupervisedProcess p in pl.Processes.Where(p => p.IsRoot || !now.Contains(p.ParentPid)))
                        {
                            procTree.AddRoot(p, parent => procs.Where(x => x.ParentPid == parent.Pid)).ExpandAll();
                        }
                        procTree.MarkDirty();
                    }
                    break;
                case Captured c:
                    snapTable.Rows.Insert(0, [c.Id, ByteFormat.Human(c.Bytes), c.Reason]);
                    banner.Content = StyledText.Of("  ✓ Captured ").Bold().Fg(Theme.Current.Success)
                        .Append($"{c.Id}").Bold().Fg(Theme.Current.Foreground)
                        .Append($" ({ByteFormat.Human(c.Bytes)}) — in the library. Snapshot another, or q to finish.").Fg(Theme.Current.Muted);
                    app.Invalidate();
                    break;
            }
        };

        // Background poller: heap + gc + process list, ~2 Hz. Pauses heap polls during a capture.
        var producer = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested && !supervisor.RootExited)
            {
                app.Post(new ProcessList(supervisor.List()));
                if (Volatile.Read(ref busy) == 0)
                {
                    int pid = supervisor.PrimaryPid;
                    app.Post(new HeapSample(supervisor.HeapSize(pid, poll), supervisor.GcCount(pid, poll)));
                }
                try { await Task.Delay(500, cancellation); }
                catch (OperationCanceledException) { break; }
            }
            app.Post(new Status("process exited — q to close.", Theme.Current.Muted));
        }, cancellation);

        app.RunAsync(cancellation).GetAwaiter().GetResult();
        _ = producer; // detached; cancellation/root-exit ends it
    }

    // A footer shortcut button: "<key> <label>" with the key char bold+warning, transparent normal
    // background, hover fills the striped-row background. Mirrors the Tessera demo's footer.
    private static Button FooterButton(string key, string label, Action onClick) => new()
    {
        Content = StyledText.Of(key).Bold().Fg(Theme.Current.Warning).Append($" {label}").Fg(Theme.Current.Muted),
        OnClick = onClick,
        Style = new Style(Theme.Current.Muted, Color.Default),
        HoverStyle = new Style(Theme.Current.Foreground, Theme.Current.StripeBackground),
        PressedStyle = new Style(Theme.Current.SelectionForeground, Theme.Current.Accent),
    };
}
