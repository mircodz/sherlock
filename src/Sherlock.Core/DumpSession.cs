using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.Analysis;

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
    private IReadOnlyList<HeapTypeStat>? _histogram;

    /// <summary>The heap's dominator tree - built once, cached.</summary>
    public DominatorTree GetDominatorTree(CancellationToken cancellationToken = default) =>
        _dominatorTree ??= new DominatorAnalyzer(this).Build(cancellationToken);

    /// <summary>The full per-type heap histogram - built once, cached. Filter in-memory.</summary>
    public IReadOnlyList<HeapTypeStat> GetHistogram() =>
        _histogram ??= new HeapAnalyzer(this).GetStatistics();

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
        Runtime.Dispose();
        DataTarget.Dispose();
    }
}
