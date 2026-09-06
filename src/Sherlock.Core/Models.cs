using System.Collections.Generic;

namespace Sherlock.Core;

/// <summary>High-level summary of the dump and its runtime, for the <c>info</c> command.</summary>
public sealed record DumpInfo(
    string DumpPath,
    long FileSizeBytes,
    string ClrFlavor,
    string ClrVersion,
    string Architecture,
    string Platform,
    int? ProcessId,
    bool ServerGc,
    int HeapCount,
    ulong TotalHeapBytes,
    int ThreadCount,
    int ModuleCount);

/// <summary>Aggregated heap statistics for one managed type (the <c>dumpheap -stat</c> view).</summary>
public sealed record HeapTypeStat(
    string TypeName,
    long Count,
    ulong TotalSize)
{
    public ulong AverageSize => Count == 0 ? 0 : TotalSize / (ulong)Count;
}

public sealed record ObjectInstance(
    ulong Address,
    string TypeName,
    ulong Size,
    string? Preview);

/// <summary>Top instances returned, plus totals over all matching objects (for "top K of N").</summary>
public sealed record InstanceListing(
    IReadOnlyList<ObjectInstance> Instances,
    long TotalMatched,
    ulong TotalMatchedSize);

/// <summary>One instance field of an inspected object.</summary>
public sealed record FieldValue(
    string Name,
    string TypeName,
    string Value,
    int Offset);

/// <summary>A bounded object preview: string contents, collection elements, or instance fields.</summary>
public sealed record ObjectDetail(
    ulong Address,
    string TypeName,
    ulong Size,
    bool IsArray,
    string? StringValue,
    int? ElementCount,
    IReadOnlyList<string> Elements,
    IReadOnlyList<FieldValue> Fields);

/// <summary>A dominator-tree node: an object plus its retained size (memory freed if it's collected).</summary>
public sealed record DominatorNode(
    ulong Address,
    string TypeName,
    ulong OwnSize,
    ulong RetainedSize);

/// <summary>A managed thread and its call stack.</summary>
public sealed record ThreadInfo(
    int ManagedThreadId,
    uint OsThreadId,
    bool IsAlive,
    bool IsGcThread,
    bool IsFinalizer,
    string? State,
    IReadOnlyList<StackFrameInfo> StackTrace);

public sealed record StackFrameInfo(
    ulong InstructionPointer,
    string Description);

public sealed record ModuleInfo(
    string Name,
    ulong ImageBase,
    ulong Size,
    bool IsDynamic);

public sealed record SegmentInfo(
    ulong Start,
    ulong End,
    ulong Length,
    string Kind);

public sealed record ExceptionInfo(
    ulong Address,
    string TypeName,
    string? Message,
    int StackFrameCount,
    int? ThreadId);

/// <summary>A group of identical string values wasting memory through duplication.</summary>
public sealed record DuplicateString(
    string Value,
    long Count,
    ulong TotalSize,
    ulong WastedBytes);

/// <summary>Finalizable objects of one type still awaiting finalization.</summary>
public sealed record FinalizableTypeStat(
    string TypeName,
    long Count,
    ulong TotalBytes);

/// <summary>Totals for objects still registered for finalization.</summary>
public sealed record FinalizerReport(
    long TotalObjects,
    ulong TotalBytes,
    IReadOnlyList<FinalizableTypeStat> ByType);

/// <summary>One subscriber type on a delegate's invocation list, and how many handlers it holds.</summary>
public sealed record HandlerTarget(
    string TypeName,
    int Count);

/// <summary>A large delegate invocation list and the subscriber types it retains.</summary>
public sealed record EventSubscription(
    ulong DelegateAddress,
    string DelegateType,
    int SubscriberCount,
    IReadOnlyList<HandlerTarget> Targets);

/// <summary>One path from a GC root to a target object, found by <c>gcroot</c>.</summary>
public sealed record GcRootPath(
    GcRootInfo Root,
    IReadOnlyList<GcRootNode> Path);

/// <summary>A GC root that keeps an object alive.</summary>
public sealed record GcRootInfo(
    ulong Address,
    string Kind,
    bool IsInterior,
    bool IsPinned);

/// <summary>A node along a GC root path: an object address and its type.</summary>
public sealed record GcRootNode(
    ulong Address,
    string TypeName);
