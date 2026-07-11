using System;
using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;
using Sherlock.Core;

namespace Sherlock.Mcp;

/// <summary>
/// Sherlock's heap-analysis surface, exposed to MCP clients. Every tool takes a snapshot id/label
/// (see <c>list_snapshots</c>) and maps straight onto the <see cref="Snapshot"/> facade.
/// </summary>
[McpServerToolType]
public static class SherlockTools
{
    [McpServerTool(Name = "list_snapshots")]
    [Description("List catalogued heap snapshots with their process, size and whether allocation provenance is available.")]
    public static object ListSnapshots(SnapshotLibrary library) => Dto.Snapshots(library.Store);

    [McpServerTool(Name = "info")]
    [Description("High-level summary of a snapshot: runtime, architecture, GC mode, heap size, thread and module counts.")]
    public static object Info(OpenSnapshots snapshots, [Description("snapshot id or label")] string snapshot) =>
        snapshots.Query(snapshot, s => Dto.Info(s.Info));

    [McpServerTool(Name = "histogram")]
    [Description("Managed types ranked by total heap size (the dumpheap -stat view). Start here to find what dominates memory.")]
    public static object Histogram(
        OpenSnapshots snapshots,
        [Description("snapshot id or label")] string snapshot,
        [Description("how many top types to return")] int top = 30) =>
        snapshots.Query(snapshot, s => Dto.Histogram(s.Histogram, top));

    [McpServerTool(Name = "dominators")]
    [Description("Objects with the largest retained size - the biggest memory holders. Retained size is what would be freed if the object became unreachable.")]
    public static object Dominators(
        OpenSnapshots snapshots,
        [Description("snapshot id or label")] string snapshot,
        [Description("how many top objects to return")] int top = 20) =>
        snapshots.Query(snapshot, s => Dto.Dominators(s.Dominators, top));

    [McpServerTool(Name = "inspect")]
    [Description("Type, size, and fields (or string/array contents) of one object at a hex address.")]
    public static object Inspect(
        OpenSnapshots snapshots,
        [Description("snapshot id or label")] string snapshot,
        [Description("object address in hex, e.g. 0x1a2b3c4d")] string address) =>
        snapshots.Query(snapshot, s => Dto.Inspect(s.Inspect(ParseAddress(address))));

    [McpServerTool(Name = "gcroot")]
    [Description("Why an object is still alive: paths from a GC root to the object at a hex address.")]
    public static object GcRoot(
        OpenSnapshots snapshots,
        [Description("snapshot id or label")] string snapshot,
        [Description("object address in hex")] string address,
        [Description("how many root paths to return")] int maxPaths = 3)
    {
        ulong target = ParseAddress(address);
        return snapshots.Query(snapshot, s => Dto.Roots(target, s.Roots(target, maxPaths)));
    }

    [McpServerTool(Name = "whoalloc")]
    [Description("The allocation call stack that created an object at a hex address. Requires a snapshot captured with allocation correlation (see list_snapshots 'correlated').")]
    public static object WhoAlloc(
        OpenSnapshots snapshots,
        [Description("snapshot id or label")] string snapshot,
        [Description("object address in hex")] string address)
    {
        ulong target = ParseAddress(address);
        return snapshots.Query(snapshot, s => Dto.WhoAllocated(target, s.WhoAllocated(target)));
    }

    private static ulong ParseAddress(string address)
    {
        string text = address.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }
        if (!ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value))
        {
            throw new DumpAnalysisException($"'{address}' is not a hex address.");
        }
        return value;
    }
}
