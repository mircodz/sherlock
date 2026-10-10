using System;
using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Lists commands, or prints usage detail for one with <c>help &lt;command&gt;</c>.</summary>
public sealed class HelpReplCommand : IReplCommand
{
    private readonly Func<IEnumerable<IReplCommand>> _commands;

    /// <param name="commands">Deferred so the list can include help itself.</param>
    public HelpReplCommand(Func<IEnumerable<IReplCommand>> commands) => _commands = commands;

    // Unlisted categories follow these.
    private static readonly string[] CategoryOrder = ["Analysis", "Allocation profiling", "Live", "Library", "Session"];

    public string Name => "help";
    public IReadOnlyList<string> Aliases => ["?", "h"];
    public string Summary => "List commands, or show one command's usage.";
    public string Usage => "help [command]";
    public int MaxArgs => 1;
    public string Category => "Session";

    public ReplResult Execute(ReplContext context, string[] args)
    {
        IReadOnlyList<IReplCommand> commands = _commands().ToList();

        if (args.Length > 0)
        {
            return PrintCommandDetail(context.Console, commands, args[0]);
        }

        IEnumerable<IGrouping<string, IReplCommand>> groups = commands
            .GroupBy(c => c.Category)
            .OrderBy(g =>
            {
                int index = Array.IndexOf(CategoryOrder, g.Key);
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(g => g.Key);

        // One grid for every section keeps the descriptions aligned; full usage is one "help <command>" away.
        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn(new TableColumn("cmd").NoWrap());
        table.AddColumn("desc");
        bool first = true;
        foreach (IGrouping<string, IReplCommand> group in groups)
        {
            if (!first)
            {
                table.AddEmptyRow();
            }
            first = false;
            table.AddRow($"[bold {Palette.Heading}]{Markup.Escape(group.Key)}[/]", "");
            foreach (IReplCommand command in group)
            {
                table.AddRow($"  [bold]{Markup.Escape(command.Name)}[/]", Markup.Escape(command.Summary));
            }
            if (group.Key == "Session")
            {
                table.AddRow("  [bold]exit[/]", "Quit Sherlock (also quit, q, or Ctrl-D).");
            }
        }
        context.Console.Write(table);
        context.Console.MarkupLine($"[{Palette.Muted}]help <command> shows its usage.[/]");
        return ReplResult.Success;
    }

    private static ReplResult PrintCommandDetail(IAnsiConsole console, IReadOnlyList<IReplCommand> commands, string name)
    {
        IReplCommand? command = commands.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) ||
            c.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase));

        if (command is null)
        {
            console.MarkupLineInterpolated($"[{Palette.Warning}]No such command:[/] {name}");
            return ReplResult.Failure;
        }

        console.MarkupLineInterpolated($"[bold]{command.Name}[/] [{Palette.Muted}]—[/] {command.Summary}");
        console.MarkupLineInterpolated($"  [{Palette.Muted}]usage[/]    {command.Usage}");
        if (command.Aliases.Count > 0)
        {
            console.MarkupLineInterpolated($"  [{Palette.Muted}]aliases[/]  {string.Join(", ", command.Aliases)}");
        }
        return ReplResult.Success;
    }
}
