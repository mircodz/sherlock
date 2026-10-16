using System;
using System.Collections.Generic;
using System.IO;
using Sherlock.Core.Profiling;

namespace Sherlock.Core.Collection;

public sealed record SnapshotCaptureResult(string DumpPath, string? ProvenancePath);

/// <summary>Captures heap/provenance artifacts, leaving cataloguing to the caller.</summary>
public static class SnapshotCapture
{
    /// <summary>The caller must serialize capture and cataloguing while using a profiler's sidecar files.</summary>
    public static SnapshotCaptureResult Collect(int pid, RunTarget? target = null)
    {
        string? provenance = null;
        string? dumpPath = null;
        try
        {
            if (target is { HasCorrelation: true })
            {
                (dumpPath, provenance) = target.CaptureCoherentSnapshot(pid);
            }
            else if (target?.AllocationPath is not null)
            {
                provenance = target.CaptureAllocations(pid, RunTarget.CaptureTimeout);
            }

            if (provenance is not null)
            {
                try
                {
                    ProvenanceReader.ValidateFile(provenance);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new DumpAnalysisException($"The profiler produced invalid allocation data for process {pid}: {ex.Message}", ex);
                }
            }

            dumpPath ??= DumpCollector.Collect(pid, DumpKind.Heap);
            return new SnapshotCaptureResult(dumpPath, provenance);
        }
        catch (Exception ex)
        {
            DumpAnalysisException failure = Failure(pid, ex, dumpPath, provenance);
            if (ReferenceEquals(failure, ex))
            {
                throw;
            }
            throw failure;
        }
    }

    /// <summary>Reports capture or cataloguing failure without deleting completed artifacts.</summary>
    public static DumpAnalysisException Failure(int pid, Exception error, SnapshotCaptureResult? capture) =>
        Failure(pid, error, capture?.DumpPath, capture?.ProvenancePath);

    internal static DumpAnalysisException Failure(int pid, Exception error, string? dumpPath, string? provenancePath)
    {
        var paths = new List<string>(2);
        if (dumpPath is not null && File.Exists(dumpPath))
        {
            paths.Add($"heap dump '{dumpPath}'");
        }
        if (provenancePath is not null && File.Exists(provenancePath))
        {
            paths.Add($"allocation data '{provenancePath}'");
        }
        string preserved = paths.Count == 0 ? string.Empty : $" Preserved {string.Join(" and ", paths)}.";
        if (error is DumpAnalysisException analysis)
        {
            return preserved.Length == 0 ? analysis : new DumpAnalysisException($"{analysis.Message}{preserved}", analysis);
        }
        return new DumpAnalysisException($"Could not create snapshot for process {pid}: {error.Message}{preserved}", error);
    }
}
