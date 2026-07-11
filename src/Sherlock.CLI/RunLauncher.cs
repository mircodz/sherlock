using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sherlock.Core;
using Sherlock.Core.Collection;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI;

/// <summary>A parsed <c>run</c> invocation: the profiler options plus the target command and its args.</summary>
public sealed record RunSpec(
    bool Profile,
    bool Correlate,
    bool CollectChildren,
    bool DumpOnCrash,
    string? SnapshotOn,
    IReadOnlyList<string> Command)
{
    /// <summary>Correlation, triggers, and child collection all ride the profiler, so it must be attached.</summary>
    public bool NeedsProfiler => Profile || Correlate || CollectChildren || SnapshotOn is not null;
}

/// <summary>The one launch path shared by <c>sl run</c> and the REPL <c>run</c> command.</summary>
public static class RunLauncher
{
    public const string Usage = "run [--profile] [--correlate] [--children] [--no-crash-dump] [--snapshot-on <event>] [--] <path> [args...]";

    /// <summary>Parses run flags from raw REPL tokens. Returns null (after printing the usage) if no command is given.</summary>
    public static RunSpec? Parse(IReadOnlyList<string> args, IAnsiConsole console)
    {
        bool profile = false, correlate = false, children = false, dumpOnCrash = true;
        string? snapshotOn = null;
        var rest = new List<string>();

        // `--` ends option parsing; the first non-flag token is the target and the rest its args.
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (rest.Count > 0) { rest.Add(arg); continue; }

            switch (arg)
            {
                case "--profile": profile = true; break;
                case "--correlate": correlate = true; break;
                case "--children": children = true; break;
                case "--no-crash-dump": dumpOnCrash = false; break;
                case "--snapshot-on" when i + 1 < args.Count: snapshotOn = args[++i]; break;
                case "--": break;
                default: rest.Add(arg); break;
            }
        }

        if (rest.Count == 0)
        {
            console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage}");
            return null;
        }
        return new RunSpec(profile, correlate, children, dumpOnCrash, snapshotOn, rest);
    }

    /// <summary>
    /// Launches the target as a live workspace target under a fresh run session. On success the
    /// supervisor is registered with the workspace (so its pollers see it) and returned; on failure
    /// the error is printed and the empty session rolled back.
    /// </summary>
    public static (ProcessSupervisor Supervisor, Session Session)? Launch(Workspace workspace, IAnsiConsole console, RunSpec spec)
    {
        string? profilerPath = null;
        if (spec.NeedsProfiler)
        {
            profilerPath = ProfilerLibrary.Locate();
            if (profilerPath is null)
            {
                console.MarkupLineInterpolated(
                    $"[red]error:[/] profiler library ({ProfilerLibrary.FileName}) not found. Build it with [bold]src/native/build.sh[/], or set [bold]SHERLOCK_PROFILER_PATH[/].");
                return null;
            }
        }

        Session session = workspace.Store.BeginSession(SessionKind.Run, command: string.Join(' ', spec.Command), withLog: true);
        var supervisor = new ProcessSupervisor { SessionId = session.Id };
        try
        {
            SupervisedProcess root = supervisor.Start(
                spec.Command[0], spec.Command.Skip(1).ToList(), dumpOnCrash: spec.DumpOnCrash, profilerPath,
                captureDir: session.Dir, snapshotOn: spec.SnapshotOn, correlate: spec.Correlate, collectChildren: spec.CollectChildren);

            ProcessRecord rootProcess = session.GetOrAddProcess(root.Pid, Path.GetFileName(spec.Command[0]), isRoot: true);
            rootProcess.Exec = spec.Command[0];
            workspace.Store.Persist(session);
            workspace.AddTarget(supervisor);

            console.MarkupLineInterpolated(
                $"[green]launched[/] {Path.GetFileName(spec.Command[0])} [grey](pid {root.Pid}) → session[/] [bold]{session.Id}[/][grey].[/]");
            return (supervisor, session);
        }
        catch (DumpAnalysisException ex)
        {
            supervisor.Dispose();
            workspace.Store.Remove(session.Id); // roll back the empty session
            console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return null;
        }
    }
}
