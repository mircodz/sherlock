using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Lists instances of a type, largest first (like SOS <c>dumpheap -type</c>).</summary>
public sealed class ObjectsReplCommand : IReplCommand
{
    private const int DefaultLimit = 20;

    public string Name => "objects";
    public IReadOnlyList<string> Aliases => ["obj", "instances"];
    public string Summary => "List instances of a type, largest first. e.g. objects System.String";
    public string Usage => "objects <type-filter> [count]";

    public void Execute(ReplContext context, string[] args)
    {
        Args.Require(args, 1, Usage);
        string filter = args[0];
        int limit = Args.Limit(args, 1, DefaultLimit);

        InstanceListing listing = context.Console.Status()
            .Start($"Scanning heap for '{filter}'…", _ =>
                context.Snapshot.Instances(filter, limit));

        if (listing.TotalMatched == 0)
        {
            context.Console.MarkupLineInterpolated($"[#FFAF00]No instances matched[/] '{filter}'.");
            return;
        }

        var table = Theme.Table(expand: true);
        table.AddColumn(new TableColumn("[bold]Address[/]"));
        table.AddColumn(new TableColumn("[bold]Size[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Type[/]"));
        table.AddColumn(new TableColumn("[bold]Value[/]"));

        foreach (ObjectInstance instance in listing.Instances)
        {
            table.AddRow(
                $"[#FFD75F]0x{instance.Address:x}[/]",
                $"[bold #F2F2F2]{ByteSize.Format((long)instance.Size)}[/]",
                $"[#00D7FF]{Markup.Escape(TypeNames.Short(instance.TypeName))}[/]",
                instance.Preview is null ? "" : $"[#F2F2F2]{Markup.Escape(instance.Preview)}[/]");
        }

        context.Console.Write(table);
        context.Console.MarkupLine(
            $"Showing top [bold]{listing.Instances.Count}[/] of [bold]{Counts.Format(listing.TotalMatched)}[/] matches, " +
            $"[bold #F2F2F2]{ByteSize.Format((long)listing.TotalMatchedSize)}[/] total. " +
            $"[#808791]Copy an address into[/] gcroot <address>.");
    }
}
