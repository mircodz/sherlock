using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// String-focused analysis: reports duplicated string values, ordered by wasted memory.
/// </summary>
public sealed class StringsReplCommand : IReplCommand
{
    private const int DefaultLimit = 20;

    public string Name => "strings";
    public IReadOnlyList<string> Aliases => ["str"];
    public string Summary => "Find duplicate string values wasting memory (most wasteful first).";
    public string Usage => "strings [count]";

    public void Execute(ReplContext context, string[] args)
    {
        // Duplicate analysis is the default (and only) mode; a leading count sets the limit.
        // `--dup`/`-d` is still accepted (and ignored) so older scripts keep working.
        int limit = DefaultLimit;
        string? countArg = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (countArg is not null && !int.TryParse(countArg, out limit))
        {
            limit = DefaultLimit;
        }

        IReadOnlyList<DuplicateString> duplicates = context.Console.Status()
            .Start("Hashing strings…", _ => new HeapAnalyzer(context.Session).FindDuplicateStrings(limit));

        if (duplicates.Count == 0)
        {
            context.Console.MarkupLine("[green]No duplicated strings found.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn(new TableColumn("[bold]Count[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Wasted[/]").RightAligned());
        table.AddColumn("[bold]Value[/]");

        ulong totalWasted = 0;
        foreach (DuplicateString dupString in duplicates)
        {
            totalWasted += dupString.WastedBytes;
            table.AddRow(
                dupString.Count.ToString("N0"),
                ByteSize.Format((long)dupString.WastedBytes),
                $"[aqua]{Markup.Escape(Preview(dupString.Value))}[/]");
        }

        context.Console.Write(table);
        context.Console.MarkupLine($"[grey]Top {duplicates.Count} duplicated strings waste[/] [bold]{ByteSize.Format((long)totalWasted)}[/].");
    }

    private static string Preview(string value)
    {
        value = value.ReplaceLineEndings(" ");
        return value.Length > 80 ? value[..80] + "…" : value;
    }
}
