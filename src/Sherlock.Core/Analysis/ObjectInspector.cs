using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>Reads a single object's identity and field values (the <c>dumpobj</c> view).</summary>
public sealed class ObjectInspector
{
    private const int StringPreviewLength = 256;

    private readonly DumpSession _session;

    public ObjectInspector(DumpSession session) => _session = session;

    /// <exception cref="DumpAnalysisException">The address is not a valid managed object.</exception>
    public ObjectDetail Inspect(ulong address)
    {
        ClrObject obj = _session.Runtime.Heap.GetObject(address);
        if (!obj.IsValid || obj.Type is null)
            throw new DumpAnalysisException($"0x{address:x} is not a valid managed object address.");

        ClrType type = obj.Type;

        var fields = new List<FieldValue>();
        foreach (ClrInstanceField field in type.Fields)
        {
            fields.Add(new FieldValue(
                Name: field.Name ?? "<field>",
                TypeName: field.Type?.Name ?? field.ElementType.ToString(),
                Value: FormatField(obj.Address, field),
                Offset: field.Offset));
        }

        return new ObjectDetail(
            Address: obj.Address,
            TypeName: type.Name ?? "<unknown>",
            Size: obj.Size,
            IsArray: type.IsArray,
            StringValue: type.IsString ? obj.AsString(StringPreviewLength) : null,
            Fields: fields);
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
                    ClrObject reference = field.ReadObject(objAddress, false);
                    return reference.IsNull
                        ? "null"
                        : $"0x{reference.Address:x} ({reference.Type?.Name ?? "?"})";

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
