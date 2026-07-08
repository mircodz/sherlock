using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Inspects a single object by address: type, size and every field value (SOS <c>dumpobj</c>).</summary>
public sealed class PrintReplCommand : IReplCommand
{
    private const int DefaultElementLimit = 20;

    public string Name => "print";
    public IReadOnlyList<string> Aliases => ["p", "do"];
    public string Summary => "Print one object by address: its type, size and fields (px for a graph).";
    public string Usage => "print <address> [element-count]";

    public void Execute(ReplContext context, string[] args)
    {
        if (args.Length == 0)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage}");
            return;
        }

        if (!Addresses.TryParse(args[0], out ulong address))
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] '{args[0]}' is not a valid object address.");
            return;
        }

        int elementLimit = args.Length > 1 && int.TryParse(args[1], out int n) && n > 0 ? n : DefaultElementLimit;

        ObjectDetail detail = new ObjectInspector(context.Session).Inspect(address);

        context.Console.MarkupLineInterpolated($"[bold]{detail.TypeName}[/]");
        context.Console.MarkupLineInterpolated($"  [grey]address[/] 0x{detail.Address:x}   [grey]size[/] [bold green]{ByteSize.Format((long)detail.Size)}[/]");

        if (detail.StringValue is not null)
        {
            context.Console.MarkupLineInterpolated($"  [grey]value[/] [aqua]\"{detail.StringValue}\"[/]");
            return;
        }

        if (detail.ElementCount is int count)
        {
            PrintElements(context.Console, detail, count, elementLimit);
            return;
        }

        if (detail.Fields.Count == 0)
        {
            context.Console.MarkupLine("  [grey]<no instance fields>[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Square);
        table.AddColumn(new TableColumn("[bold]Offset[/]").RightAligned());
        table.AddColumn("[bold]Field[/]");
        table.AddColumn("[bold]Type[/]");
        table.AddColumn("[bold]Value[/]");

        foreach (FieldValue field in detail.Fields)
        {
            table.AddRow(
                $"[grey]+0x{field.Offset:x}[/]",
                Markup.Escape(field.Name),
                Markup.Escape(TypeNames.Short(field.TypeName)),
                Markup.Escape(field.Value));
        }

        context.Console.Write(table);
    }

    private static void PrintElements(IAnsiConsole console, ObjectDetail detail, int count, int limit)
    {
        console.MarkupLineInterpolated($"  [grey]count[/] {count}");
        int shown = 0;
        foreach (string element in detail.Elements)
        {
            if (shown >= limit)
            {
                break;
            }
            console.MarkupLineInterpolated($"  {element}");
            shown++;
        }

        int remaining = count - shown;
        if (remaining > 0)
        {
            console.MarkupLineInterpolated($"  [grey]… {remaining} more (print 0x{detail.Address:x} <n> to show more)[/]");
        }
    }
}
