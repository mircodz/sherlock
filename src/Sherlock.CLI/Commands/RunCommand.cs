using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using Sherlock.CLI.Rendering;
using Sherlock.CLI.Repl;
using Sherlock.Core;
using Sherlock.Core.Collection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sherlock.CLI.Commands;

/// <summary>
/// Launches a process under supervision, tracks its .NET subtree, and lets you
/// take snapshots on demand from a small live console.
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

        [CommandOption("--no-crash-dump")]
        [Description("Do not auto-write a dump if a process crashes.")]
        public bool NoCrashDump { get; init; }

        [CommandOption("--profile")]
        [Description("Attach the Sherlock allocation profiler at startup (logs allocations).")]
        public bool Profile { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        IAnsiConsole console = AnsiConsole.Console;

        // The target path may be the positional arg, or — in `run [opts] -- <bin> <args>`
        // form — the first token after `--`. Remaining tokens are the target's args.
        var childArgs = new List<string>(settings.Args);
        childArgs.AddRange(context.Remaining.Raw);

        string path = settings.Path;
        if (string.IsNullOrEmpty(path))
        {
            if (childArgs.Count == 0)
            {
                console.MarkupLine("[red]error:[/] no executable given. Usage: [bold]run [[--profile]] -- <bin> [[args]][/]");
                return 1;
            }
            path = childArgs[0];
            childArgs.RemoveAt(0);
        }

        string? profilerPath = null;
        if (settings.Profile)
        {
            profilerPath = ProfilerLibrary.Locate();
            if (profilerPath is null)
            {
                console.MarkupLineInterpolated(
                    $"[red]error:[/] profiler library ({ProfilerLibrary.FileName}) not found. Build it with [bold]src/native/build.sh[/], or set [bold]SHERLOCK_PROFILER_PATH[/].");
                return 1;
            }
        }

        using var supervisor = new ProcessSupervisor();

        SupervisedProcess root;
        try
        {
            root = supervisor.Start(path, childArgs, dumpOnCrash: !settings.NoCrashDump, profilerPath);
        }
        catch (DumpAnalysisException ex)
        {
            console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 1;
        }

        console.MarkupLineInterpolated($"[bold]Sherlock[/] supervising [aqua]{System.IO.Path.GetFileName(path)}[/] (root pid {root.Pid}).");
        if (profilerPath is not null)
        {
            console.MarkupLineInterpolated($"[grey]Allocation profiler attached; allocations logged to[/] {supervisor.LogPath}[grey].[/]");
        }

        console.MarkupLine("Commands: [bold]ps[/], [bold]snapshot [[pid]] [[--analyze]][/], [bold]kill[/], [bold]exit[/].");
        console.WriteLine();

        return Loop(console, supervisor);
    }

    private static int Loop(IAnsiConsole console, ProcessSupervisor supervisor)
    {
        var history = new ReplHistory(null);
        while (true)
        {
            string? line = LineEditor.ReadLine("sherlock(run)> ", history);
            if (line is null)
            {
                return 0;
            }

            string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                NoteIfRootExited(console, supervisor);
                continue;
            }

            switch (tokens[0].ToLowerInvariant())
            {
                case "exit" or "quit" or "q":
                    return 0;

                case "ps" or "list":
                    PrintProcesses(console, supervisor);
                    break;

                case "snapshot" or "snap":
                    Snapshot(console, supervisor, tokens[1..]);
                    break;

                case "kill":
                    supervisor.Kill();
                    console.MarkupLine("[grey]Sent kill to the process tree.[/]");
                    break;

                case "help" or "?":
                    console.MarkupLine("[bold]ps[/] list tracked processes · [bold]snapshot [[pid]] [[--analyze]][/] dump a process · [bold]kill[/] · [bold]exit[/]");
                    break;

                default:
                    console.MarkupLineInterpolated($"[red]unknown:[/] {tokens[0]}. Try [bold]help[/].");
                    break;
            }
        }
    }

    private static void Snapshot(IAnsiConsole console, ProcessSupervisor supervisor, string[] args)
    {
        bool analyze = args.Contains("--analyze") || args.Contains("-a");
        string? pidArg = args.FirstOrDefault(a => !a.StartsWith('-'));

        int pid = supervisor.RootPid;
        if (pidArg is not null && !int.TryParse(pidArg, out pid))
        {
            console.MarkupLineInterpolated($"[red]error:[/] '{pidArg}' is not a pid.");
            return;
        }

        string path;
        try
        {
            path = console.Status().Start($"Snapshotting pid {pid}…",
                _ => DumpCollector.Collect(pid, DumpKind.Heap, outputPath: null));
        }
        catch (DumpAnalysisException ex)
        {
            console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return;
        }

        long size = new FileInfo(path).Length;
        console.MarkupLineInterpolated($"[green]✓[/] {ByteSize.Format(size)} → [aqua]{path}[/]");

        if (analyze)
        {
            console.WriteLine();
            ReplHost.OpenAndRun(console, path);
        }
    }

    private static void PrintProcesses(IAnsiConsole console, ProcessSupervisor supervisor)
    {
        IReadOnlyList<SupervisedProcess> processes = supervisor.List();
        if (processes.Count == 0)
        {
            console.MarkupLine("[yellow]No live processes.[/]");
            NoteIfRootExited(console, supervisor);
            return;
        }

        foreach (SupervisedProcess process in processes)
        {
            string role = process.IsRoot ? "[bold]root [/]" : "child";
            string net = process.IsDotnet ? "[green].NET   [/]" : "[grey]native[/]";
            console.MarkupLine($"  [grey]{process.Pid,7}[/]  {role}  {net}  {Markup.Escape(process.Name)}");
        }

        NoteIfRootExited(console, supervisor);
    }

    private static void NoteIfRootExited(IAnsiConsole console, ProcessSupervisor supervisor)
    {
        if (supervisor.RootExited)
        {
            console.MarkupLineInterpolated($"[grey](root has exited{(supervisor.RootExitCode is int c ? $", code {c}" : "")})[/]");
        }
    }
}
