using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cellar.Layout;
using Cellar.Primitives;
using Cellar.Terminal;
using Cellar.Text;
using Cellar.Widgets;
using Cellar.Widgets.Charts;
using Cellar.Widgets.Charts.Trees;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Collection;
using Sherlock.Core.Store;
using Theme = Cellar.Theming.Theme;

namespace Sherlock.CLI.Live;

/// <summary>The live process, heap, event, and snapshot context shown by <c>run --live</c>.</summary>
public static class LiveDashboard
{
    private sealed record HeapSample(int Pid, HeapStats? Heap, string? Problem);
    private sealed record ProcessList(IReadOnlyList<RunProcess> Processes);
    private sealed record Capturing(int Pid, string Name, DateTimeOffset At);
    private sealed record Captured(SnapshotEntry Entry, TimeSpan? Duration, DateTimeOffset At);
    private sealed record Status(string Text, Color Color);
    private sealed record LiveEvent(DateTimeOffset At, string Text, Color Color);

    /// <summary>Returns false when a triggered capture failed.</summary>
    public static bool Run(Workspace workspace, RunTarget target, IReadOnlyList<string> command, CancellationToken cancellation)
    {
        Sherlock.CLI.Rendering.Theme.ApplyCellar();
        using var terminal = new AnsiTerminal();
        using var app = new App(terminal);
        int eventRowCount = Math.Clamp(terminal.Size.Height / 4, 3, 8);

        using var liveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        CancellationToken liveCancellation = liveCts.Token;
        var started = Stopwatch.StartNew();
        var busy = 0;
        var triggerFailed = false;
        var requests = new ConcurrentQueue<int>();
        var selectedPid = target.Pid;
        var paused = false;
        var showLogs = false;
        DateTimeOffset killArmedUntil = DateTimeOffset.MinValue;

        var processes = new List<RunProcess>();
        var processIds = new HashSet<int>();
        var events = new List<LiveEvent>();
        var samples = new Queue<(DateTimeOffset At, long Bytes)>();
        long previousGc = -1;
        long previousHeap = -1;
        DateTimeOffset lastSampleAt = default;

        var title = new Label(StyledText.Empty());
        var status = new Label(new StyledText("select a process, then press Enter to capture", Theme.Current.MutedStyle));
        var heapRule = Section("HEAP");
        var heapStat = new Label(StyledText.Empty());
        var generationStat = new Label(StyledText.Empty());
        var heap = new Sparkline { BaselineZero = false, Capacity = 120, Color = Theme.Current.Success };

        var processTree = new TreeView<RunProcess>
        {
            RenderLabel = node =>
            {
                RunProcess process = node.Value;
                Color color = process.IsDotnet ? Theme.Current.Accent : Theme.Current.Muted;
                return StyledText.Of(process.Name).Fg(color).Underline().Link(process.Pid)
                    .Append($"  {process.Pid}").Fg(Theme.Current.Secondary)
                    .Append(process.IsDotnet ? "  .NET" : "  native").Fg(Theme.Current.Muted)
                    .Append(process.IsRoot ? "  root" : "").Fg(Theme.Current.Muted);
            },
            ShowGuides = true,
            ShowHeader = false,
            HasFocus = true, // the tree ignores navigation keys without focus
        };

        var snapshots = new Table { ShowHeader = true, Striped = true, ShowScrollbar = true };
        snapshots.Columns.Add(new Column("Id", Constraint.Length(7)));
        snapshots.Columns.Add(new Column("Size", Constraint.Length(10), Justify.Right));
        snapshots.Columns.Add(new Column("Contents", Constraint.Fill(2)));
        snapshots.Columns.Add(new Column("When", Constraint.Length(8), Justify.Right));

        var eventLines = new Label[eventRowCount];
        for (int i = 0; i < eventLines.Length; i++)
        {
            eventLines[i] = new Label(StyledText.Empty());
        }

        var heapSection = new Stack(Direction.Vertical)
            .Add(heapRule, Constraint.Length(1))
            .Add(new Padding(heapStat, new Thickness(2, 0)), Constraint.Length(1))
            .Add(new Padding(generationStat, new Thickness(2, 0)), Constraint.Length(1))
            .Add(new Padding(heap, new Thickness(2, 0)), Constraint.Fill());

        var processSection = new Stack(Direction.Vertical)
            .Add(Section("PROCESSES"), Constraint.Length(1))
            .Add(new Padding(processTree, new Thickness(1, 0)), Constraint.Fill());
        var snapshotSection = new Stack(Direction.Vertical)
            .Add(Section("SNAPSHOTS"), Constraint.Length(1))
            .Add(new Padding(snapshots, new Thickness(1, 0)), Constraint.Fill());
        Widget lower = new Stack(terminal.Size.Width >= 100 ? Direction.Horizontal : Direction.Vertical)
            .Add(processSection, Constraint.Fill())
            .Add(snapshotSection, Constraint.Fill());

        var eventRule = Section("EVENTS");
        var eventSection = new Stack(Direction.Vertical).Add(eventRule, Constraint.Length(1));
        foreach (Label line in eventLines)
        {
            eventSection.Add(new Padding(line, new Thickness(2, 0)), Constraint.Length(1));
        }

        void AddEvent(string text, Color color, DateTimeOffset? at = null)
        {
            events.Insert(0, new LiveEvent(at ?? DateTimeOffset.Now, text, color));
            if (events.Count > 32)
            {
                events.RemoveAt(events.Count - 1);
            }
            RefreshEvents();
        }

        void RefreshEvents()
        {
            eventRule.Label = StyledText.Of(showLogs ? "TARGET LOG" : "EVENTS").Bold().Fg(Theme.Current.Info);
            if (showLogs)
            {
                IReadOnlyList<string> lines = target.ReadLog(eventLines.Length);
                for (int i = 0; i < eventLines.Length; i++)
                {
                    int source = lines.Count - 1 - i;
                    eventLines[i].Content = source >= 0
                        ? new StyledText(TextUtil.Preview(lines[source], Math.Max(20, terminal.Size.Width - 4)), Theme.Current.MutedStyle)
                        : StyledText.Empty();
                }
                return;
            }

            for (int i = 0; i < eventLines.Length; i++)
            {
                if (i >= events.Count)
                {
                    eventLines[i].Content = StyledText.Empty();
                    continue;
                }
                LiveEvent entry = events[i];
                eventLines[i].Content = StyledText.Of(entry.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Fg(Theme.Current.Muted)
                    .Append("  " + entry.Text).Fg(entry.Color);
            }
        }

        void ResetHeap()
        {
            samples.Clear();
            heap.SetValues([]);
            previousGc = -1;
            previousHeap = -1;
            lastSampleAt = default;
            heapStat.Content = new StyledText("waiting for heap metrics", Theme.Current.MutedStyle);
            generationStat.Content = StyledText.Empty();
        }

        void SelectProcess(RunProcess process)
        {
            int previous = Interlocked.Exchange(ref selectedPid, process.Pid);
            heapRule.Label = StyledText.Of($"HEAP - {process.Name} [{process.Pid}]").Bold().Fg(Theme.Current.Info);
            if (previous != process.Pid)
            {
                ResetHeap();
                AddEvent($"selected {process.Name} [{process.Pid}]", Theme.Current.Info);
            }
        }

        void UpdateTitle(string state)
        {
            RunProcess? selected = processes.FirstOrDefault(process => process.Pid == Volatile.Read(ref selectedPid));
            string name = selected?.Name ?? target.Name;
            Color stateColor = state == "RUNNING" ? Theme.Current.Success : state == "EXITED" ? Theme.Current.Muted : Theme.Current.Warning;
            title.Content = StyledText.Of("sl live").Bold().Fg(Theme.Current.Success)
                .Append("  " + name).Fg(Theme.Current.Accent)
                .Append($"  pid {Volatile.Read(ref selectedPid)}").Fg(Theme.Current.Secondary)
                .Append("  " + state).Bold().Fg(stateColor)
                .Append("  " + started.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)).Fg(Theme.Current.Muted);
        }

        void Snapshot(int pid)
        {
            if (Interlocked.Exchange(ref busy, 1) == 1)
            {
                return;
            }

            string name = processes.FirstOrDefault(process => process.Pid == pid)?.Name ?? "process";
            requests.Enqueue(pid);
            app.Post(new Capturing(pid, name, DateTimeOffset.Now));
        }

        void SnapshotSelected()
        {
            if (processTree.SelectedNode is { } node)
            {
                Snapshot(node.Value.Pid);
            }
        }

        processTree.OnSelect = node => SelectProcess(node.Value);
        processTree.OnLinkClick = pid => Snapshot((int)pid);

        var footer = new Stack(Direction.Horizontal)
            .Add(FooterButton("Enter", "Snapshot", SnapshotSelected), Constraint.Length(18))
            .Add(FooterButton("l", "Events/logs", ToggleLogs), Constraint.Length(16))
            .Add(FooterButton("p", "Pause", TogglePause), Constraint.Length(11))
            .Add(FooterButton("k", "Kill x2", ArmOrKill), Constraint.Length(12))
            .Add(FooterButton("q", "Quit", app.Quit), Constraint.Length(9));

        void ArmOrKill()
        {
            DateTimeOffset now = DateTimeOffset.Now;
            if (now <= killArmedUntil)
            {
                target.Kill();
                killArmedUntil = DateTimeOffset.MinValue;
                AddEvent("process tree killed", Theme.Current.Warning, now);
                status.Content = new StyledText("process tree killed", new Style(Theme.Current.Warning, Color.Default));
                return;
            }

            killArmedUntil = now.AddSeconds(3);
            status.Content = new StyledText("press k again within 3 seconds to kill the process tree", new Style(Theme.Current.Warning, Color.Default));
            app.Invalidate();
        }

        void ToggleLogs()
        {
            showLogs = !showLogs;
            RefreshEvents();
            status.Content = new StyledText(showLogs ? "showing target log" : "showing live events", Theme.Current.MutedStyle);
            app.Invalidate();
        }

        void TogglePause()
        {
            bool next = !Volatile.Read(ref paused);
            Volatile.Write(ref paused, next);
            status.Content = new StyledText(next ? "heap sampling paused; target is still running" : "heap sampling resumed", Theme.Current.MutedStyle);
            AddEvent(next ? "heap sampling paused" : "heap sampling resumed", Theme.Current.Warning);
            app.Invalidate();
        }

        string commandText = TextUtil.Preview(string.Join(' ', command), Math.Max(20, terminal.Size.Width - 4));
        status.Content = new StyledText(commandText, Theme.Current.MutedStyle);
        app.Root = new Stack(Direction.Vertical)
            .Add(new Padding(title, new Thickness(1, 0)), Constraint.Length(1))
            .Add(new Padding(status, new Thickness(1, 0)), Constraint.Length(1))
            .Add(heapSection, Constraint.Fill(3))
            .Add(lower, Constraint.Fill(2))
            .Add(eventSection, Constraint.Length(eventLines.Length + 1))
            .Add(footer, Constraint.Length(1));

        app.OnEvent = input =>
        {
            switch (input)
            {
                case KeyEvent { IsChar: true } key when key.Rune.Value == 'q':
                    app.Quit();
                    return true;
                case KeyEvent { IsChar: true } key when key.Rune.Value == 'k':
                    ArmOrKill();
                    return true;
                case KeyEvent { IsChar: true } key when key.Rune.Value == 'l':
                    ToggleLogs();
                    return true;
                case KeyEvent { IsChar: true } key when key.Rune.Value == 'p':
                    TogglePause();
                    return true;
                case KeyEvent { Key: Key.Enter }: // the tree would also collapse an expanded parent
                    SnapshotSelected();
                    return true;
            }
            return processTree.OnEvent(input);
        };

        app.OnMessage = message =>
        {
            switch (message)
            {
                case HeapSample sample when sample.Pid == Volatile.Read(ref selectedPid):
                    UpdateTitle(sample.Heap is null ? "WAITING" : "RUNNING");
                    if (sample.Heap is not { } current)
                    {
                        if (sample.Problem is not null)
                        {
                            heapStat.Content = new StyledText($"heap metrics unavailable: {sample.Problem}", new Style(Theme.Current.Warning, Color.Default));
                            generationStat.Content = StyledText.Empty();
                        }
                        break;
                    }
                    if (current.At == lastSampleAt)
                    {
                        break;
                    }

                    lastSampleAt = current.At;
                    samples.Enqueue((current.At, current.Total));
                    while (samples.Count > 1 && current.At - samples.Peek().At > TimeSpan.FromSeconds(10))
                    {
                        samples.Dequeue();
                    }
                    long delta = current.Total - samples.Peek().Bytes;
                    string deltaText = delta >= 0 ? "+" + ByteSize.Format(delta) : "-" + ByteSize.Format(-delta);
                    heap.Push(current.Total);
                    heapStat.Content = StyledText.Of(ByteSize.Format(current.Total)).Bold().Fg(Theme.Current.Success)
                        .Append("  managed heap").Fg(Theme.Current.Muted)
                        .Append($"    {deltaText} / 10s").Fg(delta > 0 ? Theme.Current.Success : Theme.Current.Foreground)
                        .Append($"    {current.Collections} GCs observed").Fg(Theme.Current.Muted);
                    generationStat.Content = StyledText.Of("gen0 ").Fg(Theme.Current.Muted)
                        .Append(ByteSize.Format(current.Gen0)).Fg(Theme.Current.Foreground)
                        .Append("  gen1 ").Fg(Theme.Current.Muted)
                        .Append(ByteSize.Format(current.Gen1)).Fg(Theme.Current.Foreground)
                        .Append("  gen2 ").Fg(Theme.Current.Muted)
                        .Append(ByteSize.Format(current.Gen2)).Fg(Theme.Current.Foreground)
                        .Append("  LOH ").Fg(Theme.Current.Muted)
                        .Append(ByteSize.Format(current.Loh)).Fg(Theme.Current.Foreground)
                        .Append("  POH ").Fg(Theme.Current.Muted)
                        .Append(ByteSize.Format(current.Poh)).Fg(Theme.Current.Foreground);

                    if (previousGc >= 0 && current.Collections > previousGc)
                    {
                        long count = current.Collections - previousGc;
                        AddEvent($"{(count == 1 ? "GC" : $"{count} GCs")}  heap {ByteSize.Format(previousHeap)} -> {ByteSize.Format(current.Total)}",
                            Theme.Current.Info, current.At);
                    }
                    previousGc = current.Collections;
                    previousHeap = current.Total;
                    break;

                case Capturing capture:
                    status.Content = StyledText.Of("snapshotting ").Fg(Theme.Current.Warning)
                        .Append($"{capture.Name} [{capture.Pid}]").Fg(Theme.Current.Foreground);
                    AddEvent($"snapshot started for {capture.Name} [{capture.Pid}]", Theme.Current.Warning, capture.At);
                    app.Invalidate();
                    break;

                case Status update:
                    if (update.Text.StartsWith("process exited", StringComparison.Ordinal))
                    {
                        UpdateTitle("EXITED");
                    }
                    status.Content = new StyledText(update.Text, new Style(update.Color, Color.Default));
                    AddEvent(update.Text, update.Color);
                    app.Invalidate();
                    break;

                case ProcessList list:
                    processes.Clear();
                    processes.AddRange(list.Processes);
                    HashSet<int> currentIds = list.Processes.Select(process => process.Pid).ToHashSet();
                    if (!currentIds.SetEquals(processIds))
                    {
                        processIds = currentIds;
                        processTree.Clear();
                        foreach (RunProcess process in list.Processes.Where(process => process.IsRoot || !currentIds.Contains(process.ParentPid)))
                        {
                            processTree.AddRoot(process, parent => processes.Where(child => child.ParentPid == parent.Pid)).ExpandAll();
                        }
                        processTree.MarkDirty();

                        RunProcess? selected = list.Processes.FirstOrDefault(process => process.IsRoot) ?? list.Processes.FirstOrDefault();
                        if (selected is not null)
                        {
                            SelectProcess(selected);
                        }
                    }
                    if (showLogs)
                    {
                        RefreshEvents();
                    }
                    break;

                case Captured capture:
                    SnapshotEntry entry = capture.Entry;
                    string contents = entry.HasCorrelation ? "heap + alloc + corr" : entry.HasAllocations ? "heap + alloc" : "heap";
                    string size = ByteSize.Format(entry.TotalSizeBytes);
                    string took = capture.Duration is { } duration ? $" in {Duration(duration)}" : "";
                    snapshots.Rows.Insert(0,
                        [entry.Id, size, entry.Reason is { } reason ? $"{contents} ({reason})" : contents,
                         capture.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)]);
                    status.Content = StyledText.Of("captured ").Fg(Theme.Current.Success)
                        .Append(entry.Id).Bold().Fg(Theme.Current.Foreground)
                        .Append($"  {contents}  {size}{took}").Fg(Theme.Current.Muted);
                    AddEvent($"snapshot {entry.Id} captured{(entry.Reason is { } why ? $" on {why}" : "")}  {contents}  {size}{took}",
                        Theme.Current.Success, capture.At);
                    app.Invalidate();
                    break;
            }
        };

        UpdateTitle("STARTING");
        AddEvent("live view started", Theme.Current.Info);
        // Samples metrics without the profiler, so it keeps running while a capture holds the target.
        var sampler = Task.Run(async () =>
        {
            while (!liveCancellation.IsCancellationRequested && !target.HasExited)
            {
                IReadOnlyList<RunProcess> tree = target.Processes();
                app.Post(new ProcessList(tree));
                int pid = Volatile.Read(ref selectedPid);
                RunProcess? process = tree.FirstOrDefault(candidate => candidate.Pid == pid);
                RuntimeMetrics metrics = process switch
                {
                    null => new RuntimeMetrics(null, "process exited"),
                    { IsDotnet: false } => new RuntimeMetrics(null, "not a .NET process"),
                    _ => target.Metrics(pid),
                };
                if (!Volatile.Read(ref paused))
                {
                    app.Post(new HeapSample(pid, metrics.Heap, metrics.Error));
                }

                try { await Task.Delay(500, liveCancellation); }
                catch (OperationCanceledException) { break; }
            }
            if (!liveCancellation.IsCancellationRequested)
            {
                app.Post(new Status(target.ExitCode is { } code ? $"process exited  code {code}" : "process exited", Theme.Current.Muted));
            }
        }, liveCancellation);

        // The only thread that captures or writes to the store: manual snapshots, triggers, exit profiles.
        var capturer = Task.Run(async () =>
        {
            while (!liveCancellation.IsCancellationRequested)
            {
                if (requests.TryDequeue(out int pid))
                {
                    var elapsed = Stopwatch.StartNew();
                    try
                    {
                        app.Post(new Captured(workspace.Capture(pid, load: false), elapsed.Elapsed, DateTimeOffset.Now));
                    }
                    catch (Exception ex)
                    {
                        app.Post(new Status($"capture failed: {ex.Message}", Theme.Current.Error));
                    }
                    finally
                    {
                        Interlocked.Exchange(ref busy, 0);
                    }
                }

                try
                {
                    foreach (TriggeredCaptureResult capture in workspace.PollTriggeredSnapshots())
                    {
                        if (capture.Entry is { } entry)
                        {
                            app.Post(new Captured(entry, null, DateTimeOffset.Now));
                        }
                        if (capture.Error is { } error)
                        {
                            triggerFailed = true;
                            app.Post(new Status(capture.Entry is null ? $"{capture.Probe} fired but capture failed: {error}" : error, Theme.Current.Error));
                        }
                    }
                    foreach (Session session in workspace.PollExitedAllocationProfiles())
                    {
                        app.Post(new Status($"allocation profile captured for {session.Id}", Theme.Current.Success));
                    }
                }
                catch (Exception ex)
                {
                    app.Post(new Status($"capture failed: {ex.Message}", Theme.Current.Error));
                }

                try { await Task.Delay(150, liveCancellation); }
                catch (OperationCanceledException) { break; }
            }
        }, liveCancellation);

        try
        {
            app.RunAsync(liveCancellation).GetAwaiter().GetResult();
        }
        finally
        {
            liveCts.Cancel();
            foreach (Task loop in new[] { sampler, capturer })
            {
                try { loop.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            }
        }
        return !triggerFailed;
    }

    private static Rule Section(string name) => new(StyledText.Of(name).Bold().Fg(Theme.Current.Info))
    {
        LabelPosition = Justify.Left,
        Style = Theme.Current.BorderStyle,
    };

    private static string Duration(TimeSpan elapsed) => elapsed.TotalSeconds < 1
        ? elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms"
        : elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    private static Button FooterButton(string key, string label, Action onClick) => new()
    {
        Content = StyledText.Of(key).Bold().Fg(Theme.Current.Success).Append($" {label}").Fg(Theme.Current.Muted),
        OnClick = onClick,
        Style = new Style(Theme.Current.Muted, Color.Default),
        HoverStyle = new Style(Theme.Current.Foreground, Theme.Current.StripeBackground),
        PressedStyle = new Style(Theme.Current.SelectionForeground, Theme.Current.Success),
    };
}
