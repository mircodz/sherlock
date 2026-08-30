using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.Analysis;
using Sherlock.Core.HeapModel;

namespace Sherlock.Core;

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
    private HeapGraphProvider? _heapGraph;
    private IReadOnlyList<HeapTypeStat>? _histogram;

    /// <summary>The compact object graph, extracted once (bypassing ClrMD's DAC) and persisted beside
    /// the dump so reopening skips extraction. Backs the graph analyses.</summary>
    public HeapGraph GetHeapGraph(CancellationToken cancellationToken = default) =>
        (_heapGraph ??= new HeapGraphProvider(this)).Get(cancellationToken);

    /// <summary>The dominator tree over the persisted DAC-free heap graph, cached on disk beside the
    /// dump so reopening skips the recompute.</summary>
    public DominatorTree GetDominatorTree(CancellationToken cancellationToken = default) =>
        _dominatorTree ??= new DominatorAnalyzer(this).Build(cancellationToken);

    /// <summary>The full per-type heap histogram. Prefers the heap graph's type column (no ClrMD walk)
    /// when available; falls back to a ClrMD heap walk.</summary>
    public IReadOnlyList<HeapTypeStat> GetHistogram() =>
        _histogram ??= BuildHistogram();

    private IReadOnlyList<HeapTypeStat> BuildHistogram()
    {
        // An existing heap graph (from dominators, or cached on disk) gives the histogram for free via
        // its type column. Otherwise use ClrMD directly rather than pay full graph extraction.
        HeapGraphProvider provider = _heapGraph ??= new HeapGraphProvider(this);
        if (provider.TryGetCachedOrOnDisk() is { } graph && graph.Histogram() is { } rows)
        {
            var stats = new List<HeapTypeStat>(rows.Length);
            foreach ((string typeName, long count, ulong totalSize) in rows)
            {
                if (count > 0)
                {
                    stats.Add(new HeapTypeStat(typeName, count, totalSize));
                }
            }
            stats.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));
            return stats;
        }
        return new HeapAnalyzer(this).GetStatistics();
    }

    /// <summary>Opens a dump file and attaches to the first CLR runtime it contains.</summary>
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
