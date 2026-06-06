using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sherlock.CLI.Commands;
using Sherlock.Core;
using Sherlock.Core.Collection;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Launches a process under supervision; it runs in the background as a live target.</summary>
public sealed class RunReplCommand : IReplCommand
{
    public string Name => "run";
    public string Summary => "Launch a process and track it as a live target.";
    public string Usage => "run [--profile] [--] <path> [args...]";
    public string Category => "Live";

    public void Execute(ReplContext context, string[] args)
    {
        // Parse a leading `--profile` flag; `--` just separates options from the
        // target command, so the first non-flag token is the path and the rest its args.
        bool profile = false;
        var rest = new List<string>();
        foreach (string arg in args)
        {
            if (rest.Count == 0 && arg == "--profile")
            {
                profile = true;
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

        var supervisor = new ProcessSupervisor();
        try
        {
            SupervisedProcess root = supervisor.Start(rest[0], rest.Skip(1).ToList(), dumpOnCrash: true, profilerPath);
            context.Workspace.AddTarget(supervisor);
            context.Console.MarkupLineInterpolated(
                $"[green]launched[/] {Path.GetFileName(rest[0])} [grey](pid {root.Pid}). Use[/] ps[grey],[/] logs[grey],[/] snapshot[grey].[/]");
            if (profilerPath is not null)
            {
                context.Console.MarkupLine("[grey]Allocation profiler attached; allocations stream to[/] logs[grey].[/]");
            }
        }
        catch (DumpAnalysisException ex)
        {
            supervisor.Dispose();
            context.Console.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
        }
    }
}
