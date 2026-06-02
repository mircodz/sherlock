using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Shows recent command history.</summary>
public sealed class HistoryReplCommand : IReplCommand
{
    private const int DefaultCount = 20;
    private readonly ReplHistory _history;

    public HistoryReplCommand(ReplHistory history) => _history = history;

    public string Name => "history";
    public IReadOnlyList<string> Aliases => new[] { "hist" };
    public string Summary => "Show recent command history.";
    public string Usage => "history [count]";
    public string Category => "Session";

    public void Execute(ReplContext context, string[] args)
    {
        int count = DefaultCount;
        if (args.Length > 0 && int.TryParse(args[0], out int parsed))
            count = parsed;

        IReadOnlyList<string> entries = _history.Entries;
        if (entries.Count == 0)
        {
            context.Console.MarkupLine("[grey]No history yet.[/]");
            return;
        }

        int start = Math.Max(0, entries.Count - count);
        for (int i = start; i < entries.Count; i++)
            context.Console.MarkupLineInterpolated($"  [grey]{i + 1,4}[/]  {entries[i]}");
    }
}
