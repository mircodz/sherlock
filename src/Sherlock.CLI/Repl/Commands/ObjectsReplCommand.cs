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
    public int MaxArgs => 2;

    public ReplResult Execute(ReplContext context, string[] args)
    {
        Args.Require(args, 1, Usage);
        string filter = args[0];
        int limit = Args.Count(args, 1, DefaultLimit, Usage);

        InstanceListing listing = context.Console.Status()
            .Start($"Scanning heap for '{filter}'…", _ =>
                context.Snapshot.Instances(filter, limit, context.Cancellation));

        if (listing.TotalMatched == 0)
        {
            context.Console.MarkupLineInterpolated($"[{Palette.Warning}]No instances matched[/] '{filter}'.");
            return ReplResult.Success;
        }

        var table = Theme.Table(expand: true);
        table.AddColumn(new TableColumn("[bold]Address[/]"));
        table.AddColumn(new TableColumn("[bold]Size[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Type[/]"));
        table.AddColumn(new TableColumn("[bold]Value[/]"));

        foreach (ObjectInstance instance in listing.Instances)
        {
            table.AddRow(
                $"[{Palette.Address}]0x{instance.Address:x}[/]",
                $"[bold {Palette.Text}]{ByteSize.Format((long)instance.Size)}[/]",
                Styled.Type(instance.TypeName),
                instance.Preview is null ? "" : $"[{Palette.Text}]{Markup.Escape(instance.Preview)}[/]");
        }

        context.Console.Write(table);
        context.Console.MarkupLine(
            $"Showing top [bold]{listing.Instances.Count}[/] of [bold]{Counts.Format(listing.TotalMatched)}[/] matches, " +
            $"[bold {Palette.Text}]{ByteSize.Format((long)listing.TotalMatchedSize)}[/] total. " +
            $"[{Palette.Muted}]Copy an address into[/] gcroot <address>.");
        return ReplResult.Success;
    }
}
