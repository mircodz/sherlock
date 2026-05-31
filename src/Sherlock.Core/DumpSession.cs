using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.Analysis;

namespace Sherlock.Core;

/// <summary>
/// Owns an opened memory dump: the underlying <see cref="DataTarget"/> and the
/// first CLR <see cref="ClrRuntime"/> found inside it. Analyzers operate on the
/// <see cref="Runtime"/> exposed here.
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

    /// <summary>
    /// Returns the heap's dominator tree, building it on first use and caching it
    /// for the lifetime of the session (it is expensive and immutable for a dump).
    /// </summary>
    public DominatorTree GetDominatorTree(CancellationToken cancellationToken = default) =>
        _dominatorTree ??= new DominatorAnalyzer(this).Build(cancellationToken);

    /// <summary>
    /// Opens a dump file and attaches to the first CLR runtime it contains.
    /// </summary>
    /// <exception cref="FileNotFoundException">The dump file does not exist.</exception>
    /// <exception cref="DumpAnalysisException">No managed runtime could be found in the dump.</exception>
    public static DumpSession Open(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Dump file not found.", path);

        DataTarget? dataTarget = null;
        try
        {
            dataTarget = DataTarget.LoadDump(path);

            if (dataTarget.ClrVersions.Length == 0)
                throw new DumpAnalysisException(
                    "No .NET runtime was found in this dump. " +
                    "Sherlock analyzes managed (CLR) dumps; this may be a native-only process.");

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
