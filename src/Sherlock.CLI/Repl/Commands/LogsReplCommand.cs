using System.Collections.Generic;
using System.Linq;
using Sherlock.Core.Collection;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Shows captured stdout/stderr of a run target.</summary>
public sealed class LogsReplCommand : IReplCommand
{
    private const int DefaultTail = 40;

    public string Name => "logs";
    public string Summary => "Show captured stdout/stderr of a run target.";
    public string Usage => "logs [pid] [lines]";
    public string Category => "Live";

    public void Execute(ReplContext context, string[] args)
    {
        IReadOnlyList<ProcessSupervisor> targets = context.Workspace.Targets;
        if (targets.Count == 0)
        {
            context.Console.MarkupLine("[grey]No run targets. Launch one with[/] run <path>[grey].[/]");
            return;
        }

        int? pid = null;
        int tail = DefaultTail;
        foreach (string arg in args)
        {
            if (int.TryParse(arg, out int n))
            {
                // First number is a pid; a second is the line count.
                if (pid is null && n > 1000)
                {
                    pid = n;
                }
                else
                {
                    tail = n;
                }
            }
        }

        ProcessSupervisor? target = pid is int p
            ? targets.FirstOrDefault(t => t.RootPid == p)
            : targets[^1];

        if (target is null)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] no run target with pid {pid}.");
            return;
        }

        IReadOnlyList<string> lines = target.ReadLog(tail);
        if (lines.Count == 0)
        {
            context.Console.MarkupLine("[grey]<no output captured yet>[/]");
            return;
        }

        context.Console.MarkupLineInterpolated($"[grey]── {target.RootName} (pid {target.RootPid}), last {lines.Count} lines ──[/]");
        foreach (string line in lines)
        {
            context.Console.WriteLine(line);
        }
    }
}
