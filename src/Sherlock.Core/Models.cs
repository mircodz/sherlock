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

/// <summary>A single managed object instance located on the heap.</summary>
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

/// <summary>Full detail of one object. Exactly one shape is populated: a string value,
/// an enumerable's elements, or instance fields.</summary>
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

/// <summary>A single managed stack frame.</summary>
public sealed record StackFrameInfo(
    ulong InstructionPointer,
    string Description);

/// <summary>A loaded managed module/assembly.</summary>
public sealed record ModuleInfo(
    string Name,
    ulong ImageBase,
    ulong Size,
    bool IsDynamic);

/// <summary>A single GC heap segment.</summary>
public sealed record SegmentInfo(
    ulong Start,
    ulong End,
    ulong Length,
    string Kind);

/// <summary>A managed exception object found in the dump.</summary>
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

/// <summary>Objects still registered for finalization: a finalizer that was never suppressed,
/// usually a missing <c>Dispose()</c> (which would call GC.SuppressFinalize).</summary>
public sealed record FinalizerReport(
    long TotalObjects,
    ulong TotalBytes,
    IReadOnlyList<FinalizableTypeStat> ByType);

/// <summary>One subscriber type on a delegate's invocation list, and how many handlers it holds.</summary>
public sealed record HandlerTarget(
    string TypeName,
    int Count);

/// <summary>A delegate with an invocation list large enough to suspect an event-handler leak:
/// a long-lived event pinning subscribers that never unsubscribed (<c>-=</c>).</summary>
public sealed record EventSubscription(
    ulong DelegateAddress,
    string DelegateType,
    int SubscriberCount,
    IReadOnlyList<HandlerTarget> Targets);

/// <summary>One path from a GC root to a target object, found by <c>gcroot</c>.</summary>
public sealed record GcRootPath(
    string RootDescription,
    IReadOnlyList<GcRootNode> Path);

/// <summary>A node along a GC root path: an object address and its type.</summary>
public sealed record GcRootNode(
    ulong Address,
    string TypeName);
