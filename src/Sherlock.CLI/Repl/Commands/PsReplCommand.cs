using System.Collections.Generic;
using Sherlock.Core.Collection;
using Spectre.Console;
using Sherlock.CLI.Rendering;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Lists live processes across all targets launched with <c>run</c>.</summary>
public sealed class PsReplCommand : IReplCommand
{
    public string Name => "ps";
    public string Summary => "List live processes from run targets.";
    public string Usage => "ps";
    public int MaxArgs => 0;
    public string Category => "Live";

    public ReplResult Execute(ReplContext context, string[] args)
    {
        var rows = new List<RunProcess>();
        foreach (RunTarget target in context.Workspace.Targets)
        {
            rows.AddRange(target.Processes());
        }

        if (rows.Count == 0)
        {
            context.Console.MarkupLine($"[{Palette.Muted}]No live targets. Launch one with[/] run <path>[{Palette.Muted}].[/]");
            return ReplResult.Success;
        }

        foreach (RunProcess process in rows)
        {
            string role = process.IsRoot ? "[bold]root [/]" : "child";
            string net = process.IsDotnet ? $"[{Palette.Name}].NET   [/]" : $"[{Palette.Muted}]native[/]";
            context.Console.MarkupLine($"  [{Palette.Muted}]{process.Pid,7}[/]  {role}  {net}  {Markup.Escape(process.Name)}");
        }

        context.Console.MarkupLine($"[{Palette.Muted}]snapshot <pid> to dump one into the library[/]");
        return ReplResult.Success;
    }
}
