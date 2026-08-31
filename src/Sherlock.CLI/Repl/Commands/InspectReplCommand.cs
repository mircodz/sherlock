using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core.Diagnostics;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Sweeps the snapshot through every inspector (retention, event-handler leaks, finalizers, duplicate strings, fragmentation, growth) and reports the obvious problems, each pointing at the command that drills in.</summary>
public sealed class InspectReplCommand : IReplCommand
{
    public string Name => "doctor";
    public IReadOnlyList<string> Aliases => ["inspect", "leaks"];
    public string Summary => "Sweep the heap for common problems (leaks, finalizers, dup strings, growth).";
    public string Usage => "doctor";

    public void Execute(ReplContext context, string[] args)
    {
        IReadOnlyList<Finding> findings = context.Console.Status()
            .Start("Examining the heap…", _ => context.Snapshot.Diagnose());

        if (findings.Count == 0)
        {
            context.Console.MarkupLine("[#AFFF00]Clean bill of health.[/] [#808791]No obvious issues by the current heuristics.[/]");
            return;
        }

        foreach (Finding finding in findings)
        {
            string colour = finding.Severity switch
            {
                FindingSeverity.High => Theme.Error,
                FindingSeverity.Warning => Theme.Attention,
                _ => Theme.Identity,
            };

            context.Console.MarkupLineInterpolated($"[{colour}]●[/] {finding.Title}");
            context.Console.MarkupLineInterpolated($"  [#808791]{finding.Detail}[/]");
            if (finding.NextCommand is { } next)
            {
                context.Console.MarkupLineInterpolated($"  [#808791]→[/] [bold]{next}[/]");
            }
        }
    }
}
