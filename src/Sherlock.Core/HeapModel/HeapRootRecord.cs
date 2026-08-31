using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.HeapModel;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct HeapRootRecord
{
    private const uint Interior = 1;
    private const uint Pinned = 2;

    public HeapRootRecord(int objectId, ulong address, ClrRootKind kind, bool isInterior, bool isPinned)
    {
        ObjectId = objectId;
        Kind = kind;
        Address = address;
        Flags = (isInterior ? Interior : 0) | (isPinned ? Pinned : 0);
        Reserved = 0;
    }

    public readonly int ObjectId;
    public readonly ClrRootKind Kind;
    public readonly ulong Address;
    private readonly uint Flags;
    private readonly uint Reserved;

    public bool IsInterior => (Flags & Interior) != 0;
    public bool IsPinned => (Flags & Pinned) != 0;
}
