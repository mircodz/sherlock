using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
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
    public int MaxArgs => 2;
    public string Category => "Live";

    public ReplResult Execute(ReplContext context, string[] args)
    {
        IReadOnlyList<RunTarget> targets = context.Workspace.Targets;
        if (targets.Count == 0)
        {
            context.Console.MarkupLine($"[{Palette.Muted}]No run targets. Launch one with[/] run <path>[{Palette.Muted}].[/]");
            return ReplResult.Failure;
        }

        // "logs <pid> [lines]" when the first number is a target's PID, otherwise "logs [lines]".
        int? pid = null;
        int tail = DefaultTail;
        if (args.Length > 0)
        {
            int first = Args.Count(args, 0, DefaultTail, Usage);
            if (args.Length == 2 || targets.Any(t => t.Pid == first))
            {
                pid = first;
                tail = Args.Count(args, 1, DefaultTail, Usage);
            }
            else
            {
                tail = first;
            }
        }

        RunTarget? target = pid is int p
            ? targets.FirstOrDefault(t => t.Pid == p)
            : targets[^1];

        if (target is null)
        {
            Output.Error(context.Console, $"No run target with pid {pid}.");
            return ReplResult.Failure;
        }

        IReadOnlyList<string> lines = target.ReadLog(tail);
        if (lines.Count == 0)
        {
            context.Console.MarkupLine($"[{Palette.Muted}]<no output captured yet>[/]");
            return ReplResult.Success;
        }

        context.Console.MarkupLineInterpolated($"[{Palette.Muted}]── {target.Name} (pid {target.Pid}), last {lines.Count} lines ──[/]");
        foreach (string line in lines)
        {
            context.Console.WriteLine(line);
        }
        return ReplResult.Success;
    }
}
