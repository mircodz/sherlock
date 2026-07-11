using System.Collections.Generic;
using System.Linq;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Sherlock.Core.Store;

namespace Sherlock.Mcp;

/// <summary>
/// Wire shapes for tool results. Kept small and LLM-friendly: addresses are hex strings, sizes are
/// raw byte counts (the model can format), and lists are always top-N so one call can't flood context.
/// </summary>
public static class Dto
{
    public static string Hex(ulong address) => $"0x{address:x}";

    public static object Snapshots(SnapshotStore store) =>
        store.Sessions.SelectMany(session => session.Processes.SelectMany(process =>
            process.Snapshots.Select(snap => new
            {
                id = snap.Id,
                workspace = session.Id,
                process = process.Name,
                pid = process.Pid,
                created = snap.CreatedAt,
                bytes = snap.SizeBytes,
                reason = snap.Reason,
                provenance = snap.HasAllocations,
                correlated = snap.HasCorrelation,
            }))).ToList();

    public static object Info(DumpInfo i) => new
    {
        i.DumpPath,
        i.FileSizeBytes,
        clr = $"{i.ClrFlavor} {i.ClrVersion}",
        i.Architecture,
        i.Platform,
        i.ProcessId,
        i.ServerGc,
        i.HeapCount,
        i.TotalHeapBytes,
        i.ThreadCount,
        i.ModuleCount,
    };

    public static object Histogram(IReadOnlyList<HeapTypeStat> stats, int top) => new
    {
        types = stats.Count,
        totalBytes = stats.Sum(s => (long)s.TotalSize),
        top = stats
            .OrderByDescending(s => s.TotalSize)
            .Take(top)
            .Select(s => new { type = s.TypeName, count = s.Count, bytes = (long)s.TotalSize })
            .ToList(),
    };

    public static object Dominators(DominatorTree tree, int top) => new
    {
        reachableObjects = tree.ObjectCount,
        totalReachableBytes = tree.TotalReachableBytes,
        top = tree.TopDominators(top)
            .Select(n => new { address = Hex(n.Address), type = n.TypeName, ownBytes = n.OwnSize, retainedBytes = n.RetainedSize })
            .ToList(),
    };

    public static object Inspect(ObjectDetail o) => new
    {
        address = Hex(o.Address),
        type = o.TypeName,
        bytes = o.Size,
        isArray = o.IsArray,
        stringValue = o.StringValue,
        elementCount = o.ElementCount,
        elements = o.Elements,
        fields = o.Fields.Select(f => new { f.Name, type = f.TypeName, f.Value, f.Offset }).ToList(),
    };

    public static object Roots(ulong target, IReadOnlyList<GcRootPath> paths) => new
    {
        address = Hex(target),
        rooted = paths.Count > 0,
        paths = paths.Select(p => new
        {
            root = p.RootDescription,
            chain = p.Path.Select(n => new { address = Hex(n.Address), type = n.TypeName }).ToList(),
        }).ToList(),
    };

    public static object WhoAllocated(ulong target, string? stack) => new
    {
        address = Hex(target),
        tracked = stack is not null,
        // Innermost frame first, as recorded at allocation time.
        allocationStack = stack?.Split(';') ?? [],
    };
}
