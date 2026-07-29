using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Reports duplicated string values, ordered by wasted memory.</summary>
public sealed class StringsReplCommand : IReplCommand
{
    private const int DefaultLimit = 20;

    public string Name => "strings";
    public IReadOnlyList<string> Aliases => ["str"];
    public string Summary => "Find duplicate string values wasting memory.";
    public string Usage => "strings [count]";

    public void Execute(ReplContext context, string[] args)
    {
        // Leading count sets the limit; `--dup`/`-d` accepted and ignored for backward compat.
        int limit = DefaultLimit;
        string? countArg = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (countArg is not null && !int.TryParse(countArg, out limit))
        {
            limit = DefaultLimit;
        }

        IReadOnlyList<DuplicateString> duplicates = context.Console.Status()
            .Start("Hashing strings…", _ => context.Snapshot.DuplicateStrings(limit));

        if (duplicates.Count == 0)
        {
            context.Console.MarkupLine("[green]No duplicated strings found.[/]");
            return;
        }

        var table = Theme.Table(expand: true);
        table.AddColumn(new TableColumn("[bold]Count[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Wasted[/]").RightAligned());
        table.AddColumn("[bold]Value[/]");

        ulong totalWasted = 0;
        foreach (DuplicateString dupString in duplicates)
        {
            totalWasted += dupString.WastedBytes;
            table.AddRow(
                dupString.Count.ToString("N0"),
                $"[bold green]{ByteSize.Format((long)dupString.WastedBytes)}[/]",
                $"[aqua]{Markup.Escape(TextUtil.Preview(dupString.Value, 80))}[/]");
        }

        context.Console.Write(table);
        context.Console.MarkupLine($"[grey]Top {duplicates.Count} duplicated strings waste[/] [bold green]{ByteSize.Format((long)totalWasted)}[/].");
    }

}
