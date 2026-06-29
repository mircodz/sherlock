using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Inspects a single object by address: type, size and every field value (SOS <c>dumpobj</c>).</summary>
public sealed class DumpObjReplCommand : IReplCommand
{
    public string Name => "dumpobj";
    public IReadOnlyList<string> Aliases => ["do", "print", "p"];
    public string Summary => "Inspect one object by address: its type, size and fields.";
    public string Usage => "dumpobj <address>";

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

        ObjectDetail detail = new ObjectInspector(context.Session).Inspect(address);

        context.Console.MarkupLineInterpolated($"[bold]{detail.TypeName}[/]");
        context.Console.MarkupLineInterpolated($"  [grey]address[/] 0x{detail.Address:x}   [grey]size[/] {ByteSize.Format((long)detail.Size)}");

        if (detail.StringValue is not null)
        {
            context.Console.MarkupLineInterpolated($"  [grey]value[/] [aqua]\"{detail.StringValue}\"[/]");
            return;
        }

        if (detail.ElementCount is int count)
        {
            PrintElements(context.Console, detail, count);
            return;
        }

        if (detail.Fields.Count == 0)
        {
            context.Console.MarkupLine("  [grey]<no instance fields>[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("[bold]Offset[/]").RightAligned());
        table.AddColumn("[bold]Field[/]");
        table.AddColumn("[bold]Type[/]");
        table.AddColumn("[bold]Value[/]");

        foreach (FieldValue field in detail.Fields)
        {
            table.AddRow(
                $"[grey]+0x{field.Offset:x}[/]",
                Markup.Escape(field.Name),
                Markup.Escape(ShortTypeName(field.TypeName)),
                Markup.Escape(field.Value));
        }

        context.Console.Write(table);
    }

    private static void PrintElements(IAnsiConsole console, ObjectDetail detail, int count)
    {
        console.MarkupLineInterpolated($"  [grey]count[/] {count}");
        foreach (string element in detail.Elements)
            console.MarkupLineInterpolated($"  {element}");

        if (detail.Elements.Count < count)
        {
            console.MarkupLineInterpolated($"  [grey]… {count - detail.Elements.Count} more[/]");
        }
    }

    /// <summary>Trims a namespace-qualified type to its last segment for compactness.</summary>
    private static string ShortTypeName(string typeName)
    {
        int generic = typeName.IndexOf('<');
        string head = generic < 0 ? typeName : typeName[..generic];
        int dot = head.LastIndexOf('.');
        return dot < 0 ? typeName : typeName[(dot + 1)..];
    }
}
