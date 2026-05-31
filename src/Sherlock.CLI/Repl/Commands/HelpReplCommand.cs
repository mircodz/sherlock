using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// Lists the available commands, or prints usage detail for a single command
/// with <c>help &lt;command&gt;</c>.
/// </summary>
public sealed class HelpReplCommand : IReplCommand
{
    private readonly Func<IEnumerable<IReplCommand>> _commands;

    /// <param name="commands">
    /// Provider for the full command set. A delegate (rather than a list) so the
    /// registry can include this command in the very set it describes.
    /// </param>
    public HelpReplCommand(Func<IEnumerable<IReplCommand>> commands) => _commands = commands;

    public string Name => "help";
    public IReadOnlyList<string> Aliases => new[] { "?", "h" };
    public string Summary => "List commands, or `help <command>` for usage detail.";
    public string Usage => "help [command]";

    public void Execute(ReplContext context, string[] args)
    {
        IReadOnlyList<IReplCommand> commands = _commands().ToList();

        if (args.Length > 0)
        {
            PrintCommandDetail(context.Console, commands, args[0]);
            return;
        }

        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn("cmd");
        table.AddColumn("desc");
        foreach (IReplCommand command in commands)
            table.AddRow($"[bold]{Markup.Escape(command.Usage)}[/]", Markup.Escape(command.Summary));
        table.AddRow("[bold]exit[/]", "Quit Sherlock (also: quit, q, Ctrl-D).");

        context.Console.Write(table);
    }

    private static void PrintCommandDetail(IAnsiConsole console, IReadOnlyList<IReplCommand> commands, string name)
    {
        IReplCommand? command = commands.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) ||
            c.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase));

        if (command is null)
        {
            console.MarkupLineInterpolated($"[yellow]No such command:[/] {name}");
            return;
        }

        console.MarkupLineInterpolated($"[bold]{command.Name}[/] — {command.Summary}");
        console.MarkupLineInterpolated($"  usage: {command.Usage}");
        if (command.Aliases.Count > 0)
            console.MarkupLineInterpolated($"  aliases: {string.Join(", ", command.Aliases)}");
    }
}
