using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>Reads object previews and immediate children without walking the object graph.</summary>
public sealed class ObjectInspector(Snapshot snapshot)
{
    private const int StringPreviewLength = 256;
    private const int MaxElements = 100;
    private const int MaxPageSize = 1024;

    /// <exception cref="DumpAnalysisException">The address is not a valid managed object.</exception>
    public ObjectDetail Inspect(ulong address)
    {
        ClrObject obj = GetObject(address);
        ClrType type = obj.Type!;
        string? stringValue = type.IsString ? obj.AsString(StringPreviewLength) : null;
        IReadOnlyList<string> elements = [];
        IReadOnlyList<FieldValue> fields = [];
        int? elementCount = null;

        if (!type.IsString && TryGetCollection(obj, out ClrArray array, out int total))
        {
            elementCount = total;
            InspectionPage page = ReadElements(array, total, 0, MaxElements, escape: false);
            var preview = new List<string>(page.Items.Count);
            foreach (ObjectValue value in page.Items)
            {
                preview.Add($"{value.Name} {value.Value}");
            }
            elements = preview;
        }
        else if (!type.IsString)
        {
            var preview = new List<FieldValue>();
            foreach (ClrInstanceField field in type.Fields)
            {
                ObjectValue value = ReadField(obj.Address, field, interior: false, escape: false);
                preview.Add(new FieldValue(value.Name, field.Type?.Name ?? field.ElementType.ToString(), value.Value, field.Offset));
            }
            fields = preview;
        }

        return new ObjectDetail(obj.Address, type.Name ?? "<unknown>", obj.Size, type.IsArray, stringValue, elementCount, elements, fields);
    }

    public ObjectValue InspectValue(ulong address)
    {
        ClrObject obj = GetObject(address);
        try
        {
            return ReadReference("$", obj, "<unknown>", escape: true);
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            return Unreadable("$", obj.Type?.Name ?? "<unknown>", ex);
        }
    }

    /// <summary>Reads at most 1024 immediate children. Raw mode bypasses logical collection views.</summary>
    public InspectionPage InspectChildren(ObjectValue value, int startIndex = 0, int count = 64, bool raw = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidatePage(startIndex, count);
        if (!value.CanExpand)
        {
            return new InspectionPage([], 0, startIndex);
        }
        if (value.Address is not { } address || address == 0)
        {
            throw new DumpAnalysisException($"Cannot expand {value.Name}: no value address.");
        }

        if (value.Kind == ObjectValueKind.Struct)
        {
            ClrType? type = value.MethodTable is { } methodTable ? snapshot.Runtime.GetTypeByMethodTable(methodTable) : null;
            if (type is null || !type.IsValueType)
            {
                throw new DumpAnalysisException($"Cannot expand {value.Name} at 0x{address:x}: its struct type is unavailable.");
            }
            ClrValueType structure = ClrValueType.FromAddress(address, type);
            return ReadFields(structure.Address, structure.Type!, interior: true, startIndex, count);
        }

        ClrObject obj = GetObject(address);
        if (obj.Type!.IsString)
        {
            return new InspectionPage([], 0, startIndex);
        }
        if (!raw && TryGetCollection(obj, out ClrArray array, out int total))
        {
            return ReadElements(array, total, startIndex, count, escape: true) with
            {
                HasRawFields = !obj.Type.IsArray && obj.Type.Fields.Length > 0
            };
        }
        return ReadFields(address, obj.Type, interior: false, startIndex, count);
    }

    internal static void ValidatePage(int startIndex, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        if (count < 1 || count > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, $"Page size must be between 1 and {MaxPageSize}.");
        }
    }

    private ClrObject GetObject(ulong address)
    {
        ClrObject obj = snapshot.Runtime.Heap.GetObject(address);
        if (!obj.IsValid || obj.Type is null || obj.IsFree || snapshot.Runtime.Heap.GetSegmentByAddress(address) is null)
        {
            throw new DumpAnalysisException($"0x{address:x} is not a valid managed object address.");
        }
        return obj;
    }

    private bool TryGetCollection(ClrObject obj, out ClrArray array, out int total)
    {
        ClrType type = obj.Type!;
        if (type.IsArray)
        {
            array = obj.AsArray();
            total = array.Length;
            return true;
        }

        array = default;
        total = 0;
        if (type.Name?.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) != true)
        {
            return false;
        }

        // Bad collection metadata falls back to raw fields, where individual read errors remain visible.
        ClrInstanceField? itemsField = type.GetFieldByName("_items");
        ClrInstanceField? sizeField = type.GetFieldByName("_size");
        if (itemsField is null || sizeField is null ||
            !snapshot.DataTarget.DataReader.ReadPointer(itemsField.GetAddress(obj.Address), out ulong itemsAddress) ||
            !snapshot.DataTarget.DataReader.Read(sizeField.GetAddress(obj.Address), out int size))
        {
            return false;
        }
        ClrObject items = snapshot.Runtime.Heap.GetObject(itemsAddress);
        if (!items.IsValid || items.Type?.IsArray != true)
        {
            return false;
        }
        array = items.AsArray();
        if (size < 0 || size > array.Length)
        {
            return false;
        }
        total = size;
        return true;
    }

    private InspectionPage ReadElements(ClrArray array, int total, int startIndex, int count, bool escape)
    {
        int shown = Math.Min(count, Math.Max(0, total - startIndex));
        var values = new List<ObjectValue>(shown);
        ClrType? componentType = array.Type.ComponentType;
        for (int i = 0; i < shown; i++)
        {
            int index = startIndex + i;
            string name = $"[{index}]";
            string typeName = componentType?.Name ?? "<unknown>";
            try
            {
                ulong address = array.Type.GetArrayElementAddress(array.Address, index);
                values.Add(ReadValue(name, address, componentType?.ElementType ?? ClrElementType.Object, componentType, escape));
            }
            catch (Exception ex) when (IsReadError(ex))
            {
                values.Add(Unreadable(name, typeName, ex));
            }
        }
        return new InspectionPage(values, total, startIndex, total, array.Length);
    }

    private InspectionPage ReadFields(ulong address, ClrType type, bool interior, int startIndex, int count)
    {
        IReadOnlyList<ClrInstanceField> fields = type.Fields;
        int shown = Math.Min(count, Math.Max(0, fields.Count - startIndex));
        var values = new List<ObjectValue>(shown);
        for (int i = 0; i < shown; i++)
        {
            values.Add(ReadField(address, fields[startIndex + i], interior, escape: true));
        }
        return new InspectionPage(values, fields.Count, startIndex);
    }

    private ObjectValue ReadField(ulong address, ClrInstanceField field, bool interior, bool escape)
    {
        string name = field.Name ?? "<field>";
        string typeName = field.Type?.Name ?? field.ElementType.ToString();
        try
        {
            return ReadValue(name, field.GetAddress(address, interior), field.ElementType, field.Type, escape) with { Offset = field.Offset };
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            return Unreadable(name, typeName, ex) with { Offset = field.Offset };
        }
    }

    private ObjectValue ReadValue(string name, ulong address, ClrElementType element, ClrType? type, bool escape)
    {
        string typeName = type?.Name ?? element.ToString();
        ObjectValueKind kind = ObjectValueKind.Number;
        string text;
        switch (element)
        {
            case ClrElementType.Boolean:
                kind = ObjectValueKind.Boolean;
                text = Read<bool>(address).ToString();
                break;
            case ClrElementType.Char:
                kind = ObjectValueKind.Character;
                string character = Read<char>(address).ToString();
                text = $"'{(escape ? EscapePreview(character, '\'') : character)}'";
                break;
            case ClrElementType.Int8: text = Read<sbyte>(address).ToString(CultureInfo.InvariantCulture); break;
            case ClrElementType.UInt8: text = Read<byte>(address).ToString(CultureInfo.InvariantCulture); break;
            case ClrElementType.Int16: text = Read<short>(address).ToString(CultureInfo.InvariantCulture); break;
            case ClrElementType.UInt16: text = Read<ushort>(address).ToString(CultureInfo.InvariantCulture); break;
            case ClrElementType.Int32: text = Read<int>(address).ToString(CultureInfo.InvariantCulture); break;
            case ClrElementType.UInt32: text = Read<uint>(address).ToString(CultureInfo.InvariantCulture); break;
            case ClrElementType.Int64: text = Read<long>(address).ToString(CultureInfo.InvariantCulture); break;
            case ClrElementType.UInt64: text = Read<ulong>(address).ToString(CultureInfo.InvariantCulture); break;
            case ClrElementType.Float: text = Read<float>(address).ToString(CultureInfo.InvariantCulture); break;
            case ClrElementType.Double: text = Read<double>(address).ToString(CultureInfo.InvariantCulture); break;

            case ClrElementType.NativeInt:
            case ClrElementType.NativeUInt:
            case ClrElementType.Pointer:
            case ClrElementType.FunctionPointer:
                kind = ObjectValueKind.Pointer;
                text = $"0x{ReadPointer(address):x}";
                break;

            case ClrElementType.String:
            case ClrElementType.Class:
            case ClrElementType.Object:
            case ClrElementType.Array:
            case ClrElementType.SZArray:
                ulong referenceAddress = ReadPointer(address);
                if (referenceAddress == 0)
                {
                    return new ObjectValue(name, typeName, "null", ObjectValueKind.Null);
                }
                return ReadReference(name, GetObject(referenceAddress), typeName, escape);

            case ClrElementType.Struct:
                if (type is null || type.MethodTable == 0 || address == 0)
                {
                    throw new InvalidDataException("Struct address or type is unavailable.");
                }
                return new ObjectValue(name, typeName, Preview($"{{{typeName}}}", escape), ObjectValueKind.Struct, address, MethodTable: type.MethodTable);

            default:
                kind = ObjectValueKind.Other;
                text = element.ToString();
                break;
        }
        return new ObjectValue(name, typeName, text, kind);
    }

    private ObjectValue ReadReference(string name, ClrObject obj, string typeName, bool escape)
    {
        typeName = obj.Type?.Name ?? typeName;
        if (obj.Type?.IsString == true)
        {
            string text = obj.AsString(StringPreviewLength + 1) ?? throw new InvalidDataException("String contents are unavailable.");
            return new ObjectValue(name, typeName, $"\"{Preview(text, escape)}\"", ObjectValueKind.String, obj.Address, obj.Size);
        }
        return new ObjectValue(name, typeName, Preview($"0x{obj.Address:x} ({typeName})", escape), ObjectValueKind.Reference, obj.Address, obj.Size);
    }

    private T Read<T>(ulong address) where T : unmanaged
    {
        if (!snapshot.DataTarget.DataReader.Read(address, out T value))
        {
            throw new InvalidDataException($"Cannot read memory at 0x{address:x}.");
        }
        return value;
    }

    private ulong ReadPointer(ulong address)
    {
        if (!snapshot.DataTarget.DataReader.ReadPointer(address, out ulong value))
        {
            throw new InvalidDataException($"Cannot read pointer at 0x{address:x}.");
        }
        return value;
    }

    private static ObjectValue Unreadable(string name, string typeName, Exception ex) =>
        new(name, typeName, $"<unreadable: {ex.GetType().Name}>", ObjectValueKind.Unreadable);

    // ClrMD is a best-effort read boundary; lifetime, cancellation and resource failures must still surface.
    internal static bool IsReadError(Exception ex) =>
        ex is not (ObjectDisposedException or OperationCanceledException or OutOfMemoryException) &&
        ex is (IOException or InvalidDataException or InvalidOperationException or ArgumentException or ClrDiagnosticsException or DumpAnalysisException);

    private static string Preview(string text, bool escape) =>
        escape ? EscapePreview(text) : text.Length > StringPreviewLength ? text[..StringPreviewLength] + "…" : text;

    internal static string EscapePreview(string text, char quote = '"')
    {
        var result = new StringBuilder(Math.Min(text.Length, StringPreviewLength));
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            string token = c switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\0' => "\\0",
                _ when c == quote => "\\" + c,
                _ when char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) => text.Substring(i++, 2),
                _ when char.IsControl(c) || char.IsSurrogate(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator => $"\\u{(int)c:x4}",
                _ => c.ToString()
            };
            int limit = i < text.Length - 1 ? StringPreviewLength - 1 : StringPreviewLength;
            if (result.Length + token.Length > limit)
            {
                result.Append('…');
                break;
            }
            result.Append(token);
        }
        return result.ToString();
    }
}
