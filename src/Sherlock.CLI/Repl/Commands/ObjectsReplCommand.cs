using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// Lists individual object instances of a type — the SOS <c>dumpheap -type</c>
/// view — showing the largest first. e.g. <c>objects System.String</c>.
/// </summary>
public sealed class ObjectsReplCommand : IReplCommand
{
    private const int DefaultLimit = 20;

    public string Name => "objects";
    public IReadOnlyList<string> Aliases => ["obj", "instances"];
    public string Summary => "List instances of a type, largest first. e.g. objects System.String";
    public string Usage => "objects <type-filter> [count]";

    public void Execute(ReplContext context, string[] args)
    {
        if (args.Length == 0)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage}");
            return;
        }

        string filter = args[0];
        int limit = DefaultLimit;
        if (args.Length > 1 && !int.TryParse(args[1], out limit))
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] '{args[1]}' is not a valid count.");
            return;
        }

        InstanceListing listing = context.Console.Status()
            .Start($"Scanning heap for '{filter}'…", _ =>
                new HeapAnalyzer(context.Session).ListInstances(filter, limit));

        if (listing.TotalMatched == 0)
        {
            context.Console.MarkupLineInterpolated($"[yellow]No instances matched[/] '{filter}'.");
            return;
        }

        var table = new Table().Border(TableBorder.Square).Expand();
        table.AddColumn(new TableColumn("[bold]Address[/]"));
        table.AddColumn(new TableColumn("[bold]Size[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Type[/]"));
        table.AddColumn(new TableColumn("[bold]Value[/]"));

        foreach (ObjectInstance instance in listing.Instances)
        {
            table.AddRow(
                $"[grey]0x{instance.Address:x}[/]",
                $"[bold green]{ByteSize.Format((long)instance.Size)}[/]",
                $"[aqua]{Markup.Escape(TypeNames.Short(instance.TypeName))}[/]",
                instance.Preview is null ? "" : $"[gold1]{Markup.Escape(instance.Preview)}[/]");
        }

        context.Console.Write(table);
        context.Console.MarkupLine(
            $"Showing top [bold]{listing.Instances.Count}[/] of [bold]{listing.TotalMatched:N0}[/] matches, " +
            $"[bold green]{ByteSize.Format((long)listing.TotalMatchedSize)}[/] total. " +
            $"[grey]Copy an address into[/] gcroot <address>.");
    }
}
