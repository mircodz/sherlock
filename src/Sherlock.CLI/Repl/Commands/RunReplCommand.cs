using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sherlock.Core;
using Sherlock.Core.Collection;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Launches a process under supervision; it runs in the background as a live target.</summary>
public sealed class RunReplCommand : IReplCommand
{
    public string Name => "run";
    public string Summary => "Launch a process and track it as a live target.";
    public string Usage => "run [--profile] [--correlate] [--children] [--snapshot-on <event>] [--] <path> [args...]";
    public string Category => "Live";

    public void Execute(ReplContext context, string[] args)
    {
        // Parse leading flags; `--` separates options from the target command, so the
        // first non-flag token is the path and the rest its args.
        bool profile = false;
        bool correlate = false;
        bool collectChildren = false;
        string? snapshotOn = null;
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (rest.Count == 0 && arg == "--profile")
            {
                profile = true;
            }
            else if (rest.Count == 0 && arg == "--correlate")
            {
                correlate = true;
            }
            else if (rest.Count == 0 && arg == "--children")
            {
                collectChildren = true;
            }
            else if (rest.Count == 0 && arg == "--snapshot-on" && i + 1 < args.Length)
            {
                snapshotOn = args[++i];
            }
            else if (rest.Count == 0 && arg == "--")
            {
                continue;
            }
            else
            {
                rest.Add(arg);
            }
        }

        // Triggers, correlation, and child collection all ride the profiler → imply attaching it.
        if (snapshotOn is not null || correlate || collectChildren)
        {
            profile = true;
        }

        if (rest.Count == 0)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage}");
            return;
        }

        string? profilerPath = null;
        if (profile)
        {
            profilerPath = ProfilerLibrary.Locate();
            if (profilerPath is null)
            {
                context.Console.MarkupLineInterpolated(
                    $"[red]error:[/] profiler library ({ProfilerLibrary.FileName}) not found. Build it with [bold]src/native/build.sh[/], or set [bold]SHERLOCK_PROFILER_PATH[/].");
                return;
            }
        }

        Session session = context.Workspace.Store.BeginSession(
            SessionKind.Run,
            command: string.Join(' ', rest),
            withLog: true);

        var supervisor = new ProcessSupervisor { SessionId = session.Id };
        try
        {
            SupervisedProcess root = supervisor.Start(
                rest[0], rest.Skip(1).ToList(), dumpOnCrash: true, profilerPath, captureDir: session.Dir, snapshotOn: snapshotOn, correlate: correlate, collectChildren: collectChildren);
            ProcessRecord rootProcess = session.GetOrAddProcess(root.Pid, Path.GetFileName(rest[0]), isRoot: true);
            rootProcess.Exec = rest[0];
            context.Workspace.Store.Persist(session);
            context.Workspace.AddTarget(supervisor);
            context.Console.MarkupLineInterpolated(
                $"[green]launched[/] {Path.GetFileName(rest[0])} [grey](pid {root.Pid}) → session[/] [bold]{session.Id}[/][grey]. Use[/] ps[grey],[/] logs[grey],[/] snapshot[grey].[/]");
            if (snapshotOn is not null)
            {
                context.Console.MarkupLineInterpolated(
                    $"[grey]Snapshot-on[/] {snapshotOn}[grey] armed — a heap snapshot is captured into[/] [bold]{session.Id}[/][grey] when it fires.[/]");
            }
            else if (correlate)
            {
                context.Console.MarkupLine(
                    "[grey]Correlation tracking on;[/] snapshot [grey]it, then[/] whoalloc <address> [grey]to see where an object was allocated.[/]");
            }
            else if (profilerPath is not null)
            {
                context.Console.MarkupLineInterpolated(
                    $"[grey]Allocation profiler attached; profile + log under[/] {session.Dir}[grey].[/]");
            }
        }
        catch (DumpAnalysisException ex)
        {
            supervisor.Dispose();
            context.Workspace.Store.Remove(session.Id); // roll back the empty session
            context.Console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
        }
    }
}
