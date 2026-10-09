using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Inspects a single object by address: type, size and every field value (SOS <c>dumpobj</c>).</summary>
public sealed class PrintReplCommand : IReplCommand
{
    private const int DefaultElementLimit = 20;

    public string Name => "print";
    public IReadOnlyList<string> Aliases => ["p", "do"];
    public string Summary => "Print an object's type, size and fields.";
    public string Usage => "print <address> [element-count]";
    public int MaxArgs => 2;

    public ReplResult Execute(ReplContext context, string[] args)
    {
        ulong address = Args.Address(args, 0, Usage);
        int elementLimit = Args.Count(args, 1, DefaultElementLimit, Usage);

        ObjectDetail detail = context.Snapshot.Inspect(address);

        context.Console.MarkupLine(Styled.Object(detail.TypeName, detail.Address, (long)detail.Size));
        if (TypeNames.Namespace(detail.TypeName) is { Length: > 0 } ns)
        {
            context.Console.MarkupLineInterpolated($"  [{Palette.Muted}]namespace[/] {ns}");
        }

        if (detail.StringValue is not null)
        {
            context.Console.MarkupLineInterpolated($"  [{Palette.Muted}]value[/] [{Palette.Name}]\"{detail.StringValue}\"[/]");
            return ReplResult.Success;
        }

        if (detail.ElementCount is int count)
        {
            PrintElements(context.Console, detail, count, elementLimit);
            return ReplResult.Success;
        }

        if (detail.Fields.Count == 0)
        {
            context.Console.MarkupLine($"  [{Palette.Muted}]<no instance fields>[/]");
            return ReplResult.Success;
        }

        var table = Theme.Table();
        table.AddColumn(new TableColumn("[bold]Offset[/]").RightAligned());
        table.AddColumn("[bold]Field[/]");
        table.AddColumn("[bold]Type[/]");
        table.AddColumn("[bold]Value[/]");

        foreach (FieldValue field in detail.Fields)
        {
            table.AddRow(
                $"[{Palette.Muted}]+0x{field.Offset:x}[/]",
                Markup.Escape(field.Name),
                Styled.Type(field.TypeName),
                Markup.Escape(field.Value));
        }

        context.Console.Write(table);
        return ReplResult.Success;
    }

    private static void PrintElements(IAnsiConsole console, ObjectDetail detail, int count, int limit)
    {
        console.MarkupLineInterpolated($"  [{Palette.Muted}]count[/] {count}");
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
            console.MarkupLineInterpolated($"  [{Palette.Muted}]… {remaining} more (print 0x{detail.Address:x} <n> to show more)[/]");
        }
    }
}
