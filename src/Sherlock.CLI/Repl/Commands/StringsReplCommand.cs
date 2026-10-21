using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Heap;
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
    public int MaxArgs => 1;

    public ReplResult Execute(ReplContext context, string[] args)
    {
        int limit = Args.Count(args, 0, DefaultLimit, Usage);

        IReadOnlyList<DuplicateString> duplicates = context.Console.Status()
            .Start("Hashing strings…", _ => context.Snapshot.DuplicateStrings(limit, context.Cancellation));

        if (duplicates.Count == 0)
        {
            context.Console.MarkupLine($"[{Palette.Hot}]No duplicated strings found.[/]");
            return ReplResult.Success;
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
                Counts.Format(dupString.Count),
                $"[bold {Palette.Hot}]{ByteSize.Format((long)dupString.WastedBytes)}[/]",
                $"[{Palette.Name}]{Markup.Escape(Printable.Escape(dupString.Value, 80, '"'))}[/]");
        }

        context.Console.Write(table);
        context.Console.MarkupLine($"[{Palette.Muted}]Top {duplicates.Count} duplicated strings waste[/] [bold {Palette.Hot}]{ByteSize.Format((long)totalWasted)}[/].");
        return ReplResult.Success;
    }

}
