using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Collection;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Dumps a live target into the library and loads it.</summary>
public sealed class SnapshotReplCommand : IReplCommand
{
    public string Name => "snapshot";
    public IReadOnlyList<string> Aliases => ["snap", "collect"];
    public string Summary => "Snapshot a live .NET process into the library (default: the live app; `snapshot <pid>` for a specific one).";
    public string Usage => "snapshot [pid | --pid N | --name X]";
    public string Category => "Live";

    public void Execute(ReplContext context, string[] args)
    {
        int pid;
        if (args.Length > 0)
        {
            if (!TryResolvePid(context.Console, args, out pid))
            {
                return;
            }
        }
        else
        {
            // No pid: pick from live .NET processes. Prefer the single app child (target under a
            // launcher like `dotnet run`), else the single live process, else make the user choose.
            List<RunProcess> live = context.Workspace.Targets
                .SelectMany(t => t.Processes())
                .Where(p => p.IsDotnet)
                .ToList();
            List<RunProcess> children = live.Where(p => !p.IsRoot).ToList();

            RunProcess? pick =
                children.Count == 1 ? children[0] :
                live.Count == 1 ? live[0] :
                null;

            if (pick is null)
            {
                if (live.Count == 0)
                {
                    Output.Error(context.Console, $"No live .NET target. Launch one with [bold]run[/], or provide a pid.");
                }
                else
                {
                    Output.Warning(context.Console, $"Multiple live .NET processes; use [bold]snapshot <pid>[/]:");
                    foreach (RunProcess p in live)
                    {
                        context.Console.MarkupLineInterpolated($"    [#FFD75F]{p.Pid}[/]  [#00D7FF]{p.Name}[/]  [#808791]{(p.IsRoot ? "root" : "child")}[/]");
                    }
                }
                return;
            }
            pid = pick.Pid;
        }

        Capture(context, pid);
    }

    private static bool TryResolvePid(IAnsiConsole console, string[] args, out int pid)
    {
        pid = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pid" && i + 1 < args.Length &&
                int.TryParse(args[i + 1], out pid))
            {
                return true;
            }
            if (args[i] == "--name" && i + 1 < args.Length)
            {
                IReadOnlyList<DotnetProcess> matches = ProcessLocator.FindByName(args[i + 1]);
                if (matches.Count == 1)
                {
                    pid = matches[0].Pid;
                    return true;
                }
                string error = matches.Count == 0
                    ? $"no .NET process matches '{args[i + 1]}'."
                    : $"'{args[i + 1]}' is ambiguous ({matches.Count} matches); use --pid.";
                Output.Error(console, $"{error}");
                return false;
            }
            if (!args[i].StartsWith('-') && int.TryParse(args[i], out pid))
            {
                return true;
            }
        }

        Output.Error(console, $"Specify a pid, [bold]--pid N[/], or [bold]--name X[/].");
        return false;
    }

    private static void Capture(ReplContext context, int pid)
    {
        CaptureResult result;
        try
        {
            result = context.Console.Status().Start($"Snapshotting pid {pid}…", _ => context.Workspace.Capture(pid));
        }
        catch (DumpAnalysisException ex)
        {
            Output.Error(context.Console, $"{ex.Message}");
            return;
        }

        string contents = result.Entry.HasAllocations
            ? result.Entry.HasCorrelation ? "heap + allocations + correlation" : "heap + allocations"
            : "heap only";
        string sizes = result.Entry.HasAllocations
            ? $"{ByteSize.Format(result.Entry.SizeBytes)} heap + {ByteSize.Format(result.Entry.ProvenanceSizeBytes)} allocations"
            : ByteSize.Format(result.Entry.SizeBytes);
        Output.Success(context.Console, $"Saved and loaded [bold]{result.Entry.Id}[/] [#808791]({contents} · {sizes})[/]");

        switch (result.Provenance)
        {
            case ProvenanceState.Drifted:
                Output.Warning(context.Console, $"A GC ran during capture; allocation totals remain available, but object correlation was disabled.");
                break;
            case ProvenanceState.Exact:
                Output.Info(context.Console, $"Allocation correlation is exact · use [bold]whoalloc <address>[/].");
                break;
            case ProvenanceState.Unverified:
                Output.Warning(context.Console, $"Correlation could not be verified; allocation totals remain available, but object correlation was disabled.");
                break;
        }
    }
}
