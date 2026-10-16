using System;
using System.Collections.Generic;
using Spectre.Console;
using Sherlock.CLI.Rendering;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Shows recent command history.</summary>
public sealed class HistoryReplCommand(ReplHistory history) : IReplCommand
{
    private const int DefaultCount = 20;

    public string Name => "history";
    public string Summary => "Show recent command history.";
    public string Usage => "history [count]";
    public int MaxArgs => 1;
    public string Category => "Session";

    public ReplResult Execute(ReplContext context, string[] args)
    {
        int count = Args.Count(args, 0, DefaultCount, Usage);

        IReadOnlyList<string> entries = history.Entries;
        int start = Math.Max(0, entries.Count - count);
        for (int i = start; i < entries.Count; i++)
        {
            context.Console.MarkupLineInterpolated($"  [{Palette.Muted}]{i + 1,4}[/]  {entries[i]}");
        }
        return ReplResult.Success;
    }
}
