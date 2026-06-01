using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>Reads a single object's identity and contents (the <c>dumpobj</c> view).</summary>
public sealed class ObjectInspector
{
    private const int StringPreviewLength = 256;
    private const int MaxElements = 100;

    private readonly DumpSession _session;

    public ObjectInspector(DumpSession session) => _session = session;

    /// <exception cref="DumpAnalysisException">The address is not a valid managed object.</exception>
    public ObjectDetail Inspect(ulong address)
    {
        ClrObject obj = _session.Runtime.Heap.GetObject(address);
        if (!obj.IsValid || obj.Type is null)
            throw new DumpAnalysisException($"0x{address:x} is not a valid managed object address.");

        ClrType type = obj.Type;

        string? stringValue = type.IsString ? obj.AsString(StringPreviewLength) : null;

        // Arrays and the common collections are best shown as elements, not fields.
        IReadOnlyList<string> elements = Array.Empty<string>();
        int? elementCount = null;
        if (stringValue is null)
            elementCount = TryEnumerate(obj, type, out elements);

        // Only fall back to raw fields when there's nothing more meaningful to show.
        IReadOnlyList<FieldValue> fields = (stringValue is null && elementCount is null)
            ? ReadFields(obj, type)
            : Array.Empty<FieldValue>();

        return new ObjectDetail(
            Address: obj.Address,
            TypeName: type.Name ?? "<unknown>",
            Size: obj.Size,
            IsArray: type.IsArray,
            StringValue: stringValue,
            ElementCount: elementCount,
            Elements: elements,
            Fields: fields);
    }

    /// <summary>
    /// If <paramref name="obj"/> is an array or a supported collection, returns its
    /// logical length and a capped, formatted preview of its elements. Otherwise null.
    /// </summary>
    private static int? TryEnumerate(ClrObject obj, ClrType type, out IReadOnlyList<string> elements)
    {
        elements = Array.Empty<string>();

        if (type.IsArray)
        {
            ClrArray array = obj.AsArray();
            elements = FormatElements(array, type.ComponentType, array.Length);
            return array.Length;
        }

        // List<T>: a logical view over its backing array, bounded by _size.
        if (type.Name is { } name && name.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal))
        {
            ClrObject items = obj.ReadObjectField("_items");
            if (items.IsValid && items.Type?.IsArray == true)
            {
                int size = obj.ReadField<int>("_size");
                ClrArray array = items.AsArray();
                int count = Math.Clamp(size, 0, array.Length);
                elements = FormatElements(array, items.Type.ComponentType, count);
                return count;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> FormatElements(ClrArray array, ClrType? componentType, int count)
    {
        int shown = Math.Min(count, MaxElements);
        var result = new List<string>(shown);
        for (int i = 0; i < shown; i++)
            result.Add($"[{i}] {FormatElement(array, componentType, i)}");
        return result;
    }

    private static string FormatElement(ClrArray array, ClrType? componentType, int index)
    {
        ClrElementType element = componentType?.ElementType ?? ClrElementType.Object;
        try
        {
            switch (element)
            {
                case ClrElementType.Boolean: return array.GetValue<bool>(index).ToString();
                case ClrElementType.Char: return $"'{array.GetValue<char>(index)}'";
                case ClrElementType.Int8: return array.GetValue<sbyte>(index).ToString();
                case ClrElementType.UInt8: return array.GetValue<byte>(index).ToString();
                case ClrElementType.Int16: return array.GetValue<short>(index).ToString();
                case ClrElementType.UInt16: return array.GetValue<ushort>(index).ToString();
                case ClrElementType.Int32: return array.GetValue<int>(index).ToString();
                case ClrElementType.UInt32: return array.GetValue<uint>(index).ToString();
                case ClrElementType.Int64: return array.GetValue<long>(index).ToString();
                case ClrElementType.UInt64: return array.GetValue<ulong>(index).ToString();
                case ClrElementType.Float: return array.GetValue<float>(index).ToString();
                case ClrElementType.Double: return array.GetValue<double>(index).ToString();

                case ClrElementType.NativeInt:
                case ClrElementType.NativeUInt:
                case ClrElementType.Pointer:
                case ClrElementType.FunctionPointer:
                    return $"0x{(ulong)array.GetValue<nint>(index):x}";

                case ClrElementType.Struct:
                    ClrValueType value = array.GetStructValue(index);
                    return $"{{{value.Type?.Name ?? componentType?.Name ?? "struct"}}}";

                default: // String, Class, Object, Array, SZArray — reference elements.
                    return FormatReference(array.GetObjectValue(index));
            }
        }
        catch (Exception ex)
        {
            return $"<unreadable: {ex.GetType().Name}>";
        }
    }

    private static string FormatReference(ClrObject reference)
    {
        if (reference.IsNull)
            return "null";
        if (reference.Type?.IsString == true)
            return $"\"{reference.AsString(StringPreviewLength)}\"";
        return $"0x{reference.Address:x} ({reference.Type?.Name ?? "?"})";
    }

    private static IReadOnlyList<FieldValue> ReadFields(ClrObject obj, ClrType type)
    {
        var fields = new List<FieldValue>();
        foreach (ClrInstanceField field in type.Fields)
        {
            fields.Add(new FieldValue(
                Name: field.Name ?? "<field>",
                TypeName: field.Type?.Name ?? field.ElementType.ToString(),
                Value: FormatField(obj.Address, field),
                Offset: field.Offset));
        }
        return fields;
    }

    private static string FormatField(ulong objAddress, ClrInstanceField field)
    {
        try
        {
            switch (field.ElementType)
            {
                case ClrElementType.Boolean: return field.Read<bool>(objAddress, interior: false).ToString();
                case ClrElementType.Char: return $"'{field.Read<char>(objAddress, false)}'";
                case ClrElementType.Int8: return field.Read<sbyte>(objAddress, false).ToString();
                case ClrElementType.UInt8: return field.Read<byte>(objAddress, false).ToString();
                case ClrElementType.Int16: return field.Read<short>(objAddress, false).ToString();
                case ClrElementType.UInt16: return field.Read<ushort>(objAddress, false).ToString();
                case ClrElementType.Int32: return field.Read<int>(objAddress, false).ToString();
                case ClrElementType.UInt32: return field.Read<uint>(objAddress, false).ToString();
                case ClrElementType.Int64: return field.Read<long>(objAddress, false).ToString();
                case ClrElementType.UInt64: return field.Read<ulong>(objAddress, false).ToString();
                case ClrElementType.Float: return field.Read<float>(objAddress, false).ToString();
                case ClrElementType.Double: return field.Read<double>(objAddress, false).ToString();

                case ClrElementType.NativeInt:
                case ClrElementType.NativeUInt:
                case ClrElementType.Pointer:
                case ClrElementType.FunctionPointer:
                    return $"0x{field.Read<ulong>(objAddress, false):x}";

                case ClrElementType.String:
                    string? s = field.ReadString(objAddress, false);
                    if (s is null)
                        return "null";
                    return s.Length > StringPreviewLength
                        ? $"\"{s[..StringPreviewLength]}…\""
                        : $"\"{s}\"";

                case ClrElementType.Class:
                case ClrElementType.Object:
                case ClrElementType.Array:
                case ClrElementType.SZArray:
                    return FormatReference(field.ReadObject(objAddress, false));

                case ClrElementType.Struct:
                    return $"{{{field.Type?.Name ?? "struct"}}}";

                default:
                    return field.ElementType.ToString();
            }
        }
        catch (Exception ex)
        {
            return $"<unreadable: {ex.GetType().Name}>";
        }
    }
}
