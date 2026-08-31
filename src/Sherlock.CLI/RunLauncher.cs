using System;
using System.Collections.Generic;
using System.IO;
using Sherlock.Core.Collection;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI;

/// <summary>Parses and starts runs for both the CLI and REPL.</summary>
public static class RunLauncher
{
    public const string Usage = "run [--profile] [--correlate] [--children] [--snapshot-on <event>] [--profiler-log <level>] [--] <path> [args...]";

    public static RunOptions? Parse(IReadOnlyList<string> args, IAnsiConsole console)
    {
        bool profile = false, correlate = false, children = false;
        string? snapshotOn = null;
        ProfilerLogLevel logLevel = ProfilerLogLevel.Warning;
        var command = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (command.Count > 0) { command.Add(arg); continue; }
            switch (arg)
            {
                case "--profile": profile = true; break;
                case "--correlate": correlate = true; break;
                case "--children": children = true; break;
                case "--snapshot-on" when i + 1 < args.Count: snapshotOn = args[++i]; break;
                case "--profiler-log" when i + 1 < args.Count:
                    if (!Enum.TryParse(args[++i], true, out logLevel) || !Enum.IsDefined(logLevel))
                    {
                        console.MarkupLine("[red]error:[/] profiler log level must be trace, info, warning, error, or off.");
                        return null;
                    }
                    break;
                case "--profiler-log":
                    console.MarkupLine("[red]error:[/] --profiler-log requires a level.");
                    return null;
                case "--": break;
                default: command.Add(arg); break;
            }
        }

        if (command.Count == 0)
        {
            console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage}");
            return null;
        }
        return new RunOptions { Command = command, Profile = profile, Correlate = correlate, CollectChildren = children, SnapshotOn = snapshotOn, ProfilerLogLevel = logLevel };
    }

    public static (RunTarget Target, Session Session)? Launch(Workspace workspace, IAnsiConsole console, RunOptions options)
    {
        Session session = workspace.Store.BeginSession(SessionKind.Run, string.Join(' ', options.Command), withLog: true);
        RunTarget? target = null;
        try
        {
            target = RunTarget.Start(options with { OutputDirectory = session.Dir });
            ProcessRecord process = session.GetOrAddProcess(target.Pid, target.Name, isRoot: true);
            process.Exec = options.Command[0];
            workspace.Store.Persist(session);
            workspace.AddTarget(target, session);
            console.MarkupLineInterpolated($"[green]launched[/] {Path.GetFileName(options.Command[0])} [grey](pid {target.Pid}) → session[/] [bold]{session.Id}[/][grey].[/]");
            return (target, session);
        }
        catch (Exception ex)
        {
            target?.Kill();
            target?.Dispose();
            workspace.Store.Remove(session.Id);
            console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return null;
        }
    }
}
