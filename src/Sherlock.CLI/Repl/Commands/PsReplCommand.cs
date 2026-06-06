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
        var rows = new List<SupervisedProcess>();
        foreach (ProcessSupervisor supervisor in context.Workspace.Targets)
            rows.AddRange(supervisor.List());

        if (rows.Count == 0)
        {
            context.Console.MarkupLine("[grey]No live targets. Launch one with[/] run <path>[grey].[/]");
            return;
        }

        foreach (SupervisedProcess process in rows)
        {
            string role = process.IsRoot ? "[bold]root [/]" : "child";
            string net = process.IsDotnet ? "[green].NET   [/]" : "[grey]native[/]";
            context.Console.MarkupLine($"  [grey]{process.Pid,7}[/]  {role}  {net}  {Markup.Escape(process.Name)}");
        }

        context.Console.MarkupLine("[grey]snapshot <pid> to dump one into the library[/]");
    }
}
