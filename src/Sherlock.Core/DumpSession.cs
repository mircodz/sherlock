using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.Analysis;
using Sherlock.Core.HeapModel;

namespace Sherlock.Core;

/// <summary>
/// An open dump and its analysis facade. A dump is immutable, so expensive whole-heap
/// results (dominator tree, type histogram) are computed once and cached for the session's
/// lifetime; loading another snapshot creates a fresh session with a fresh cache.
/// </summary>
public sealed class DumpSession : IDisposable
{
    private DumpSession(string path, DataTarget dataTarget, ClrInfo clrInfo, ClrRuntime runtime)
    {
        DumpPath = path;
        DataTarget = dataTarget;
        ClrInfo = clrInfo;
        Runtime = runtime;
    }

    public string DumpPath { get; }
    public DataTarget DataTarget { get; }
    public ClrInfo ClrInfo { get; }
    public ClrRuntime Runtime { get; }

    private DominatorTree? _dominatorTree;
    private DominatorTree? _dominatorTreeV2;
    private HeapGraphProvider? _heapGraph;
    private IReadOnlyList<HeapTypeStat>? _histogram;

    /// <summary>The heap's dominator tree - built once, cached.</summary>
    public DominatorTree GetDominatorTree(CancellationToken cancellationToken = default) =>
        _dominatorTree ??= new DominatorAnalyzer(this).Build(cancellationToken);

    /// <summary>The compact object graph (V2): extracted once by bypassing ClrMD's DAC and persisted
    /// beside the dump, so reopening the snapshot skips extraction. Backs the V2 analyses.</summary>
    public HeapModel.HeapGraph GetHeapGraph(CancellationToken cancellationToken = default) =>
        (_heapGraph ??= new HeapGraphProvider(this)).Get(cancellationToken);

    /// <summary>The heap's dominator tree via the V2 graph pipeline - built once, cached. Same result
    /// as <see cref="GetDominatorTree"/>; kept separate while V2 is opt-in.</summary>
    public DominatorTree GetDominatorTreeV2(CancellationToken cancellationToken = default) =>
        _dominatorTreeV2 ??= new DominatorAnalyzerV2(this).Build(cancellationToken);

    /// <summary>The full per-type heap histogram - built once, cached. Filter in-memory. Prefers the
    /// V2 heap graph's type column (no ClrMD walk) when it's available; falls back to a ClrMD heap walk.</summary>
    public IReadOnlyList<HeapTypeStat> GetHistogram() =>
        _histogram ??= BuildHistogram();

    private IReadOnlyList<HeapTypeStat> BuildHistogram()
    {
        // If a heap graph already exists (built by dominators, or cached on disk beside the dump), its
        // type column gives the histogram for free — no extra heap walk. Otherwise use ClrMD directly
        // rather than pay the full graph extraction just for a histogram.
        HeapModel.HeapGraphProvider provider = _heapGraph ??= new HeapGraphProvider(this);
        if (provider.TryGetCachedOrOnDisk() is { } graph && graph.Histogram() is { } rows)
        {
            var stats = new List<HeapTypeStat>(rows.Length);
            foreach ((string typeName, long count, ulong totalSize) in rows)
            {
                if (count > 0) stats.Add(new HeapTypeStat(typeName, count, totalSize));
            }
            stats.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));
            return stats;
        }
        return new HeapAnalyzer(this).GetStatistics();
    }

    /// <summary>
    /// Opens a dump file and attaches to the first CLR runtime it contains.
    /// </summary>
    /// <exception cref="FileNotFoundException">The dump file does not exist.</exception>
    /// <exception cref="DumpAnalysisException">No managed runtime could be found in the dump.</exception>
    public static DumpSession Open(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Dump file not found.", path);
        }

        DataTarget? dataTarget = null;
        try
        {
            dataTarget = DataTarget.LoadDump(path);

            if (dataTarget.ClrVersions.Length == 0)
            {
                throw new DumpAnalysisException(
                    "No .NET runtime was found in this dump. " +
                    "Sherlock analyzes managed (CLR) dumps; this may be a native-only process.");
            }

            ClrInfo clrInfo = dataTarget.ClrVersions[0];
            ClrRuntime runtime = clrInfo.CreateRuntime();
            return new DumpSession(path, dataTarget, clrInfo, runtime);
        }
        catch
        {
            dataTarget?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _heapGraph?.Dispose();
        Runtime.Dispose();
        DataTarget.Dispose();
    }
}
