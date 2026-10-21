using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Heap;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct HeapRootRecord
{
    private const ushort Interior = 1;
    private const ushort Pinned = 2;

    public HeapRootRecord(int objectId, ulong address, ClrRootKind kind, bool isInterior, bool isPinned)
    {
        ObjectId = objectId;
        _kind = checked((ushort)kind);
        _flags = (ushort)((isInterior ? Interior : 0) | (isPinned ? Pinned : 0));
        Address = address;
    }

    public readonly int ObjectId;
    private readonly ushort _kind;
    private readonly ushort _flags;
    public readonly ulong Address;

    public ClrRootKind Kind => (ClrRootKind)_kind;
    public bool IsInterior => (_flags & Interior) != 0;
    public bool IsPinned => (_flags & Pinned) != 0;
}

/// <summary>The size of an object of 4 GiB or more, which the graph's size column marks with <see cref="uint.MaxValue"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct LargeSize(int id, ulong size)
{
    public readonly int Id = id;
    public readonly ulong Size = size;
}

/// <summary>A heap graph's identity and what it was built from.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct GraphMeta(System.Guid id, ulong freeBytes, long freeCount, long sourceLength, long sourceModified)
{
    public readonly System.Guid Id = id;
    public readonly ulong FreeBytes = freeBytes;
    public readonly long FreeCount = freeCount;
    public readonly long SourceLength = sourceLength;
    public readonly long SourceModified = sourceModified;
}
