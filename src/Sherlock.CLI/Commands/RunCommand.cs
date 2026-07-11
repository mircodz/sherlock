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

/// <summary>
/// Launches a process under supervision, runs it to completion while capturing triggered snapshots
/// and exit-time artifacts (crash dump, allocation profile) into the library, then exits. For
/// interactive, on-demand snapshotting use the REPL's <c>run</c> instead.
/// </summary>
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

        [CommandOption("--no-crash-dump")]
        [Description("Do not auto-write a dump if a process crashes.")]
        public bool NoCrashDump { get; init; }

        [CommandOption("--snapshot-on <EVENT>")]
        [Description("Capture a snapshot when an event fires, e.g. throw:My.Namespace.Exception.")]
        public string? SnapshotOn { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        IAnsiConsole console = AnsiConsole.Console;

        // The target may be the positional arg or - in `run [opts] -- <bin> <args>` form - the
        // tokens after `--`. Everything after the path is the target's own args.
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

        var spec = new RunSpec(settings.Profile, settings.Correlate, settings.Children, !settings.NoCrashDump, settings.SnapshotOn, command);

        using Workspace workspace = ReplHost.CreateWorkspace();
        if (RunLauncher.Launch(workspace, console, spec) is not { } launched)
        {
            return 1;
        }

        (ProcessSupervisor supervisor, Session session) = launched;
        console.WriteLine();

        // Stream the child's output and fire triggered snapshots while it runs.
        long logPos = 0;
        while (!supervisor.RootExited && !cancellation.IsCancellationRequested)
        {
            logPos = StreamLog(supervisor, logPos);
            PumpCaptures(workspace, console);
            Thread.Sleep(120);
        }

        if (cancellation.IsCancellationRequested)
        {
            supervisor.Kill();
            console.MarkupLine("[grey](interrupted)[/]");
        }

        logPos = StreamLog(supervisor, logPos);

        // Exit-time artifacts (crash dump, allocation profile) take a moment to flush after exit.
        if (spec.NeedsProfiler || supervisor.RootExitCode is int c && c != 0)
        {
            for (int i = 0; i < 20 && !cancellation.IsCancellationRequested; i++)
            {
                PumpCaptures(workspace, console);
                Thread.Sleep(150);
            }
        }
        StreamLog(supervisor, logPos);

        Summarize(console, workspace, session, supervisor);
        return 0;
    }

    /// <summary>Drains the run-target pollers, announcing anything captured. Returns whether anything was.</summary>
    private static bool PumpCaptures(Workspace workspace, IAnsiConsole console)
    {
        bool any = false;
        foreach (SnapshotEntry entry in workspace.PollExitedCrashDumps())
        {
            any = true;
            console.MarkupLineInterpolated($"[yellow]· crash dump[/] [bold]{entry.Id}[/] [grey]captured[/]");
        }
        foreach (Session session in workspace.PollExitedAllocationProfiles())
        {
            any = true;
            console.MarkupLineInterpolated($"[yellow]· allocation profile captured for[/] [bold]{session.Id}[/]");
        }
        foreach ((SnapshotEntry entry, string probe) in workspace.PollProbeSnapshots())
        {
            any = true;
            console.MarkupLineInterpolated($"[yellow]●[/] [bold]{probe}[/] [yellow]fired → snapshot[/] [bold]{entry.Id}[/]");
        }
        return any;
    }

    /// <summary>Writes any log content past <paramref name="pos"/> straight to stdout, returning the new position.</summary>
    private static long StreamLog(ProcessSupervisor supervisor, long pos)
    {
        string? path = supervisor.LogPath;
        if (path is null || !File.Exists(path))
        {
            return pos;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= pos)
            {
                return pos;
            }
            stream.Seek(pos, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            Console.Out.Write(reader.ReadToEnd());
            Console.Out.Flush();
            return stream.Length;
        }
        catch
        {
            return pos;
        }
    }

    private static void Summarize(IAnsiConsole console, Workspace workspace, Session session, ProcessSupervisor supervisor)
    {
        console.WriteLine();
        Session current = workspace.Store.GetSession(session.Id) ?? session;
        List<SnapshotEntry> snapshots = current.Snapshots.ToList();
        string exit = supervisor.RootExitCode is int code ? $"exit code {code}" : "still running";

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
}
