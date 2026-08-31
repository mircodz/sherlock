using System.Collections.Generic;
using Sherlock.Core.Collection;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Lists live processes across all targets launched with <c>run</c>.</summary>
public sealed class PsReplCommand : IReplCommand
{
    public string Name => "ps";
    public string Summary => "List live processes from run targets.";
    public string Usage => "ps";
    public string Category => "Live";

    public void Execute(ReplContext context, string[] args)
    {
        var rows = new List<RunProcess>();
        foreach (RunTarget target in context.Workspace.Targets)
        {
            rows.AddRange(target.Processes());
        }

        if (rows.Count == 0)
        {
            context.Console.MarkupLine("[#808791]No live targets. Launch one with[/] run <path>[#808791].[/]");
            return;
        }

        foreach (RunProcess process in rows)
        {
            string role = process.IsRoot ? "[bold]root [/]" : "child";
            string net = process.IsDotnet ? "[#00D7FF].NET   [/]" : "[#808791]native[/]";
            context.Console.MarkupLine($"  [#808791]{process.Pid,7}[/]  {role}  {net}  {Markup.Escape(process.Name)}");
        }

        context.Console.MarkupLine("[#808791]snapshot <pid> to dump one into the library[/]");
    }
}
