using System;
using System.Collections.Generic;
using System.ComponentModel;
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

        [CommandOption("--live")]
        [Description("Open a live TUI: heap usage + process tree; snapshot a process on demand.")]
        public bool Live { get; init; }

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

        var spec = new RunSpec(settings.Profile, settings.Correlate, settings.Children, !settings.NoCrashDump, settings.SnapshotOn, command, settings.Live);

        if (spec.Live && !spec.NeedsProfiler)
        {
            console.MarkupLine("[yellow]--live needs the profiler for the heap graph[/] — add [bold]--profile[/] (or [bold]--correlate[/]).");
            return 1;
        }

        using Workspace workspace = ReplHost.CreateWorkspace();
        if (RunLauncher.Launch(workspace, console, spec) is not { } launched)
        {
            return 1;
        }

        (ProcessSupervisor supervisor, Session session) = launched;
        console.WriteLine();

        if (spec.Live)
        {
            Live.LiveDashboard.Run(workspace, supervisor, spec, cancellation);
        }
        else
        {
            SupervisedRun.Drain(workspace, console, supervisor, cancellation, waitForArtifacts: spec.NeedsProfiler);
        }

        Summarize(console, workspace, session, supervisor);
        return 0;
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
