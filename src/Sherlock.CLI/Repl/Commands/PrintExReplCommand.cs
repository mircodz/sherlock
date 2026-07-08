using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// Fancy print (<c>px</c>): a depth-limited, cycle-detecting object graph — the object, its
/// reference fields, and what they point at, expanded a few levels. Complements <c>print</c>
/// (<c>p</c>), which prints one object's fields flat.
/// </summary>
public sealed class PrintExReplCommand : IReplCommand
{
    private const int DefaultDepth = 2;
    private const int MaxChildren = 12;

    public string Name => "printx";
    public IReadOnlyList<string> Aliases => ["px"];
    public string Summary => "Print an object graph (reference tree) to a depth. `print`/`p` for one object.";
    public string Usage => "printx <address> [depth]";

    public void Execute(ReplContext context, string[] args)
    {
        if (args.Length == 0 || !Addresses.TryParse(args[0], out ulong address))
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage}");
            return;
        }

        int depth = args.Length > 1 && int.TryParse(args[1], out int d) && d >= 0 ? d : DefaultDepth;

        ClrHeap heap = context.Session.Runtime.Heap;
        ClrObject root = heap.GetObject(address);
        if (!root.IsValid || root.Type is null)
        {
            context.Console.MarkupLineInterpolated($"[yellow]No object at[/] 0x{address:x}.");
            return;
        }

        var tree = new Tree(Label(root)) { Style = new Style(foreground: Color.Grey) };
        var visited = new HashSet<ulong> { root.Address };
        AddChildren(tree, root, depth, visited);
        context.Console.Write(tree);
    }

    private static void AddChildren(IHasTreeNodes parent, ClrObject obj, int depth, HashSet<ulong> visited)
    {
        if (obj.Type is null)
        {
            return;
        }

        // Scalar/string fields as inline values: `Name = "foobar"`, `Age = 42`.
        foreach ((string name, string value) in ScalarFields(obj))
        {
            parent.AddNode($"{Markup.Escape(name)} [grey]=[/] {value}");
        }

        if (depth <= 0)
        {
            return;
        }

        // Reference fields (and array elements) as edges, recursed to the next level.
        int shown = 0;
        foreach ((string edge, ClrObject child) in ObjectFields(obj))
        {
            if (shown++ >= MaxChildren)
            {
                parent.AddNode("[grey]… more references (raise depth/limit)[/]");
                break;
            }

            if (!visited.Add(child.Address))
            {
                parent.AddNode($"{Markup.Escape(edge)} [grey]→[/] {Label(child)} [grey](seen)[/]");
                continue;
            }

            TreeNode node = parent.AddNode($"{Markup.Escape(edge)} [grey]→[/] {Label(child)}");
            AddChildren(node, child, depth - 1, visited);
        }
    }

    /// <summary>Primitive and string fields, formatted as printable values.</summary>
    private static IEnumerable<(string Name, string Value)> ScalarFields(ClrObject obj)
    {
        if (obj.Type is null || obj.IsArray)
        {
            yield break;
        }

        int shown = 0;
        foreach (ClrInstanceField field in obj.Type.Fields)
        {
            if (shown >= MaxChildren)
            {
                yield break;
            }

            string? value = ScalarValue(field, obj.Address);
            if (value is not null)
            {
                shown++;
                yield return (FieldName(field.Name), value);
            }
        }
    }

    /// <summary>Reference fields (non-string) and array elements — the recursable edges.</summary>
    private static IEnumerable<(string Edge, ClrObject Target)> ObjectFields(ClrObject obj)
    {
        if (obj.IsArray)
        {
            ClrArray array = obj.AsArray();
            int len = array.Length;
            for (int i = 0; i < len && i < MaxChildren; i++)
            {
                ClrObject el = default;
                try { el = array.GetObjectValue(i); } catch { continue; }
                if (el.IsValid && !el.IsNull)
                {
                    yield return ($"[{i}]", el);
                }
            }
            yield break;
        }

        if (obj.Type is null)
        {
            yield break;
        }

        foreach (ClrInstanceField field in obj.Type.Fields)
        {
            if (!field.IsObjectReference || field.ElementType == ClrElementType.String)
            {
                continue; // strings are shown as scalar values, not recursed
            }

            ClrObject target = default;
            try { target = field.ReadObject(obj.Address, interior: false); } catch { continue; }
            if (target.IsValid && !target.IsNull)
            {
                yield return (FieldName(field.Name), target);
            }
        }
    }

    /// <summary>Reads a primitive/string field as a display string, or null if it isn't scalar.</summary>
    private static string? ScalarValue(ClrInstanceField field, ulong addr)
    {
        try
        {
            return field.ElementType switch
            {
                ClrElementType.Boolean => field.Read<bool>(addr, false) ? "true" : "false",
                ClrElementType.Char => $"'{field.Read<char>(addr, false)}'",
                ClrElementType.Int8 => field.Read<sbyte>(addr, false).ToString(),
                ClrElementType.UInt8 => field.Read<byte>(addr, false).ToString(),
                ClrElementType.Int16 => field.Read<short>(addr, false).ToString(),
                ClrElementType.UInt16 => field.Read<ushort>(addr, false).ToString(),
                ClrElementType.Int32 => field.Read<int>(addr, false).ToString(),
                ClrElementType.UInt32 => field.Read<uint>(addr, false).ToString(),
                ClrElementType.Int64 => field.Read<long>(addr, false).ToString(),
                ClrElementType.UInt64 => field.Read<ulong>(addr, false).ToString(),
                ClrElementType.Float => field.Read<float>(addr, false).ToString(),
                ClrElementType.Double => field.Read<double>(addr, false).ToString(),
                ClrElementType.NativeInt or ClrElementType.NativeUInt or ClrElementType.Pointer
                    => "0x" + field.Read<nuint>(addr, false).ToString("x"),
                ClrElementType.String => FormatString(field.ReadString(addr, false)),
                _ => null, // object references and value types are handled elsewhere
            };
        }
        catch
        {
            return null;
        }
    }

    private static string FormatString(string? value) =>
        value is null ? "[grey]null[/]" : $"[gold1]\"{Markup.Escape(TextUtil.Preview(value, 48))}\"[/]";

    /// <summary>Unwraps an auto-property backing field <c>&lt;Name&gt;k__BackingField</c> to <c>Name</c>.</summary>
    private static string FieldName(string? name)
    {
        if (name is null)
        {
            return "<field>";
        }

        if (name.StartsWith('<') && name.EndsWith(">k__BackingField"))
        {
            int end = name.IndexOf('>');
            if (end > 1)
            {
                return name[1..end];
            }
        }

        return name;
    }

    private static string Label(ClrObject obj)
    {
        string type = obj.Type?.Name ?? "<unknown>";
        return $"[aqua]{Markup.Escape(TypeNames.Short(type))}[/] [grey]0x{obj.Address:x} ·[/] [bold green]{ByteSize.Format((long)obj.Size)}[/]";
    }
}
