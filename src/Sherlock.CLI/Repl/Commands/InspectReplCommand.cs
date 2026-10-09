using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core.Diagnostics;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Reports heap findings with follow-up commands.</summary>
public sealed class InspectReplCommand : IReplCommand
{
    public string Name => "doctor";
    public IReadOnlyList<string> Aliases => ["inspect", "leaks"];
    public string Summary => "Sweep the heap for common problems (leaks, finalizers, dup strings, growth).";
    public string Usage => "doctor";
    public int MaxArgs => 0;

    public ReplResult Execute(ReplContext context, string[] args)
    {
        IReadOnlyList<Finding> findings = context.Console.Status()
            .Start("Examining the heap…", _ => context.Snapshot.Diagnose(context.Cancellation));

        if (findings.Count == 0)
        {
            context.Console.MarkupLine($"[{Palette.Hot}]Clean bill of health.[/] [{Palette.Muted}]No obvious issues by the current heuristics.[/]");
            return ReplResult.Success;
        }

        foreach (Finding finding in findings)
        {
            string colour = finding.Severity switch
            {
                FindingSeverity.High => Palette.Error,
                FindingSeverity.Warning => Palette.Warning,
                _ => Palette.Name,
            };

            context.Console.MarkupLineInterpolated($"[{colour}]●[/] {finding.Title}");
            context.Console.MarkupLineInterpolated($"  [{Palette.Muted}]{finding.Detail}[/]");
            if (finding.NextCommand is { } next)
            {
                context.Console.MarkupLineInterpolated($"  [{Palette.Muted}]→[/] [bold]{next}[/]");
            }
        }
        return ReplResult.Success;
    }
}
