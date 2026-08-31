using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using Sherlock.Core.Collection;
using Sherlock.Core.Store;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sherlock.CLI.Commands;

/// <summary>Runs a process and captures its requested artifacts.</summary>
public sealed class RunCommand : Command<RunCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[path]")]
        [Description("Executable or dll to launch (or pass it after `--`).")]
        public string Path { get; init; } = string.Empty;

        [CommandArgument(1, "[args]")]
        [Description("Arguments passed to the launched process.")]
        public string[] Args { get; init; } = [];

        [CommandOption("--profile")]
        [Description("Attach the allocation profiler; capture the exit-time allocation profile.")]
        public bool Profile { get; init; }

        [CommandOption("--correlate")]
        [Description("Track per-object allocation provenance (enables whoalloc on snapshots).")]
        public bool Correlate { get; init; }

        [CommandOption("--children")]
        [Description("Also capture allocation profiles for child processes, not just the root.")]
        public bool Children { get; init; }

        [CommandOption("--live")]
        [Description("Open a live TUI: heap usage + process tree; snapshot a process on demand.")]
        public bool Live { get; init; }

        [CommandOption("--snapshot-on <EVENT>")]
        [Description("Capture a snapshot when an event fires, e.g. throw:My.Namespace.Exception.")]
        public string? SnapshotOn { get; init; }

        [CommandOption("--profiler-log <LEVEL>")]
        [Description("Native profiler log level: trace, info, warning, error, or off.")]
        public ProfilerLogLevel ProfilerLogLevel { get; init; } = ProfilerLogLevel.Warning;
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        IAnsiConsole console = AnsiConsole.Console;

        // The target is the positional arg or, in `run [opts] -- <bin> <args>` form, the tokens
        // after `--`. Everything after the path is the target's own args.
        var command = new List<string>();
        if (!string.IsNullOrEmpty(settings.Path))
        {
            command.Add(settings.Path);
        }
        command.AddRange(settings.Args);
        command.AddRange(context.Remaining.Raw);

        if (command.Count == 0)
        {
            console.MarkupLineInterpolated($"[red]error:[/] no executable given. Usage: {RunLauncher.Usage}");
            return 1;
        }

        var options = new RunOptions { Command = command, Profile = settings.Profile, Correlate = settings.Correlate, CollectChildren = settings.Children, SnapshotOn = settings.SnapshotOn, ProfilerLogLevel = settings.ProfilerLogLevel };

        if (settings.Live && !options.NeedsProfiler)
        {
            console.MarkupLine("[yellow]--live needs the profiler for the heap graph[/] — add [bold]--profile[/] (or [bold]--correlate[/]).");
            return 1;
        }

        using Workspace workspace = ReplHost.CreateWorkspace();
        if (RunLauncher.Launch(workspace, console, options) is not { } launched)
        {
            return 1;
        }

        (RunTarget target, Session session) = launched;
        console.WriteLine();

        if (settings.Live)
        {
            Live.LiveDashboard.Run(workspace, target, options.Command, cancellation);
        }
        else
        {
            Drain(workspace, console, target, cancellation, waitForArtifacts: options.NeedsProfiler);
        }

        Summarize(console, workspace, session, target);
        return 0;
    }

    private static void Summarize(IAnsiConsole console, Workspace workspace, Session session, RunTarget target)
    {
        console.WriteLine();
        Session current = workspace.Store.GetSession(session.Id) ?? session;
        List<SnapshotEntry> snapshots = current.Snapshots.ToList();
        string exit = target.ExitCode is int code ? $"exit code {code}" : "still running";

        console.MarkupLineInterpolated($"[bold]{current.Id}[/] [grey]({exit}) — {snapshots.Count} snapshot(s)[/]");
        foreach (SnapshotEntry snapshot in snapshots)
        {
            console.MarkupLineInterpolated($"  [aqua]{snapshot.Id}[/] [grey]{snapshot.Reason ?? string.Empty}[/]");
        }

        if (snapshots.Count > 0)
        {
            console.MarkupLineInterpolated($"[grey]Analyze with[/] sl [grey](then[/] load {snapshots[0].Id}[grey]) or[/] sl mcp[grey].[/]");
        }
        else
        {
            console.MarkupLine("[grey]No snapshots captured. Try[/] --snapshot-on <event> [grey]or[/] --correlate[grey].[/]");
        }
    }

    private static void Drain(
        Workspace workspace,
        IAnsiConsole console,
        RunTarget target,
        CancellationToken cancellation,
        bool waitForArtifacts)
    {
        long logPosition = 0;
        while (!target.HasExited && !cancellation.IsCancellationRequested)
        {
            logPosition = StreamLog(target, logPosition);
            PumpCaptures(workspace, console);
            Thread.Sleep(120);
        }

        if (cancellation.IsCancellationRequested)
        {
            target.Kill();
            console.MarkupLine("[grey](interrupted)[/]");
        }

        logPosition = StreamLog(target, logPosition);
        if (waitForArtifacts)
        {
            for (int i = 0; i < 20 && !cancellation.IsCancellationRequested; i++)
            {
                PumpCaptures(workspace, console);
                Thread.Sleep(150);
            }
        }
        StreamLog(target, logPosition);
    }

    private static void PumpCaptures(Workspace workspace, IAnsiConsole console)
    {
        foreach (Session session in workspace.PollExitedAllocationProfiles())
        {
            console.MarkupLineInterpolated(
                $"[yellow]· allocation profile captured for[/] [bold]{session.Id}[/]");
        }
        foreach (TriggeredCaptureResult capture in workspace.PollTriggeredSnapshots())
        {
            if (capture.Entry is { } entry)
            {
                string contents = entry.HasAllocations ? "heap + allocations" : "heap only";
                console.MarkupLineInterpolated(
                    $"[yellow]●[/] [bold]{capture.Probe}[/] [yellow]fired → snapshot[/] [bold]{entry.Id}[/] [grey]({contents})[/]");
            }
            else
            {
                console.MarkupLineInterpolated(
                    $"[red]●[/] [bold]{capture.Probe}[/] [red]fired but capture failed:[/] {capture.Error}");
            }
        }
    }

    private static long StreamLog(RunTarget target, long position)
    {
        string? path = target.LogPath;
        if (path is null || !File.Exists(path))
        {
            return position;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= position)
            {
                return position;
            }
            stream.Seek(position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!line.StartsWith("[createdump]", StringComparison.Ordinal))
                {
                    Console.Out.WriteLine(line);
                }
            }
            Console.Out.Flush();
            return stream.Length;
        }
        catch (IOException)
        {
            return position;
        }
        catch (UnauthorizedAccessException)
        {
            return position;
        }
    }
}
