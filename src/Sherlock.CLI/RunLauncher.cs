using System;
using System.Collections.Generic;
using System.IO;
using Sherlock.CLI.Rendering;
using Sherlock.Core.Collection;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI;

/// <summary>Parses and starts runs for both the CLI and REPL.</summary>
public static class RunLauncher
{
    public const string Usage = "run [--profile] [--correlate] [--children] [--include-process <glob>] [--experimental-gc-barrier] [--snapshot-on <event>] [--profiler-log <level>] [--] <path> [args...]";

    public static RunOptions? Parse(IReadOnlyList<string> args, IAnsiConsole console)
    {
        bool profile = false, correlate = false, children = false, experimentalGcBarrier = false;
        string? snapshotOn = null;
        ProfilerLogLevel logLevel = ProfilerLogLevel.Warning;
        var command = new List<string>();
        var includeProcesses = new List<string>();
        bool forwarding = false;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (forwarding || command.Count > 0) { command.Add(arg); continue; }
            switch (arg)
            {
                case "--profile": profile = true; break;
                case "--correlate": correlate = true; break;
                case "--children": children = true; break;
                case "--include-process" when i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    includeProcesses.Add(args[++i]);
                    break;
                case "--include-process":
                    Output.Error(console, $"[bold]--include-process[/] requires a filename glob.");
                    return null;
                case "--experimental-gc-barrier": experimentalGcBarrier = true; break;
                case "--snapshot-on" when i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    snapshotOn = args[++i];
                    break;
                case "--snapshot-on":
                    Output.Error(console, $"[bold]--snapshot-on[/] requires an event.");
                    return null;
                case "--profiler-log" when i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    if (!Enum.TryParse(args[++i], true, out logLevel) || !Enum.IsDefined(logLevel))
                    {
                        Output.Error(console, $"Profiler log level must be trace, info, warning, error, or off.");
                        return null;
                    }
                    break;
                case "--profiler-log":
                    Output.Error(console, $"[bold]--profiler-log[/] requires a level.");
                    return null;
                case "--live":
                    Output.Error(console, $"[bold]--live[/] is available only with the top-level [bold]sl run[/] command.");
                    return null;
                case "--": forwarding = true; break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        Output.Error(console, $"Unknown run option: {arg}");
                        return null;
                    }
                    command.Add(arg);
                    break;
            }
        }

        var options = new RunOptions { Command = command, Profile = profile, Correlate = correlate, CollectChildren = children, IncludeProcesses = includeProcesses, ExperimentalGcBarrier = experimentalGcBarrier, SnapshotOn = snapshotOn, ProfilerLogLevel = logLevel };
        try
        {
            options.Validate();
            return options;
        }
        catch (ArgumentException ex)
        {
            Output.Error(console, $"{ex.Message} Usage: [bold]{Usage}[/]");
            return null;
        }
    }

    public static (RunTarget Target, Session Session)? Launch(Workspace workspace, IAnsiConsole console, RunOptions options)
    {
        options.Validate();
        Session session = workspace.Store.BeginSession(SessionKind.Run, string.Join(' ', options.Command), withLog: true);
        RunTarget? target = null;
        try
        {
            target = RunTarget.Start(options with { OutputDirectory = session.Dir });
            ProcessRecord process = session.GetOrAddProcess(target.Pid, target.Name, isRoot: true);
            process.Exec = options.Command[0];
            workspace.Store.Persist(session);
            workspace.AddTarget(target, session);
            Output.Success(console, $"Launched [#00D7FF]{Path.GetFileName(options.Command[0])}[/] · pid {target.Pid} · workspace [bold]{session.Id}[/]");
            return (target, session);
        }
        catch (Exception ex)
        {
            target?.Kill();
            target?.Dispose();
            workspace.Store.Remove(session.Id);
            if (ex is OperationCanceledException)
            {
                throw;
            }
            Output.Error(console, $"{ex.Message}");
            return null;
        }
    }
}
