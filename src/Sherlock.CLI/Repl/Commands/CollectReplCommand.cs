using System.Collections.Generic;
using Sherlock.Core.Collection;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Collects a dump from a live .NET process into the library and loads it.</summary>
public sealed class CollectReplCommand : IReplCommand
{
    public string Name => "collect";
    public string Summary => "Collect a dump from a live .NET process (--pid N or --name X).";
    public string Usage => "collect <pid | --pid N | --name X>";
    public string Category => "Live";

    public void Execute(ReplContext context, string[] args)
    {
        if (args.Length == 0)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage}  (or [bold]ps[/] to list processes)");
            return;
        }

        if (!TryResolvePid(context.Console, args, out int pid))
        {
            return;
        }

        SnapshotReplCommand.Collect(context, pid);
    }

    private static bool TryResolvePid(IAnsiConsole console, string[] args, out int pid)
    {
        pid = 0;

        // --pid N, --name X, or a bare numeric pid.
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pid" && i + 1 < args.Length && int.TryParse(args[i + 1], out pid))
            {
                return true;
            }

            if (args[i] == "--name" && i + 1 < args.Length)
            {
                return ResolveName(console, args[i + 1], out pid);
            }

            if (!args[i].StartsWith('-') && int.TryParse(args[i], out pid))
            {
                return true;
            }
        }

        console.MarkupLine("[red]error:[/] specify a pid, [bold]--pid N[/], or [bold]--name X[/].");
        return false;
    }

    private static bool ResolveName(IAnsiConsole console, string name, out int pid)
    {
        pid = 0;
        IReadOnlyList<DotnetProcess> matches = ProcessLocator.FindByName(name);
        switch (matches.Count)
        {
            case 0:
                console.MarkupLineInterpolated($"[red]error:[/] no .NET process matches '{name}'.");
                return false;
            case 1:
                pid = matches[0].Pid;
                return true;
            default:
                console.MarkupLineInterpolated($"[red]error:[/] '{name}' is ambiguous ({matches.Count} matches); use --pid.");
                return false;
        }
    }
}
