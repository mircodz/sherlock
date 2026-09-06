using System.Collections.Generic;

namespace Sherlock.Core.Analysis;

public enum ObjectValueKind
{
    Number,
    Boolean,
    Character,
    String,
    Reference,
    Struct,
    Null,
    Pointer,
    Unreadable,
    Other
}

/// <summary>A bounded preview. Struct addresses point to interior data, not managed objects.</summary>
public sealed record ObjectValue(
    string Name,
    string TypeName,
    string Value,
    ObjectValueKind Kind,
    ulong? Address = null,
    ulong? Size = null,
    ulong? MethodTable = null,
    int? Offset = null)
{
    public bool CanExpand => Kind is ObjectValueKind.Reference or ObjectValueKind.Struct;
}

public sealed record InspectionPage(
    IReadOnlyList<ObjectValue> Items,
    int TotalCount,
    int StartIndex,
    int? CollectionCount = null,
    int? Capacity = null)
{
    public bool HasRawFields { get; init; }
    public bool HasMore => StartIndex + Items.Count < TotalCount;
    public int NextIndex => StartIndex + Items.Count;
}
