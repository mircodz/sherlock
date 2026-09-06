using System;
using System.Collections.Generic;
using System.IO;
using Sherlock.Core.Profiling;

namespace Sherlock.Core.Collection;

/// <summary>Whether a snapshot carries allocation provenance, and if the address join is trustworthy.</summary>
public enum ProvenanceState
{
    None,
    Exact,
    Drifted,
    Unverified,
}

public sealed record SnapshotCaptureResult(string DumpPath, string? ProvenancePath, ProvenanceState Provenance);

/// <summary>Captures and checks heap/provenance artifacts, leaving cataloguing to the caller.</summary>
public static class SnapshotCapture
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DriftTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The caller must serialize capture and cataloguing while using a profiler's sidecar files.</summary>
    public static SnapshotCaptureResult Collect(int pid, RunTarget? target = null)
    {
        bool profiled = target?.AllocationPath is not null;
        bool correlationRequested = target is { HasCorrelation: true };
        bool coherentCapture = correlationRequested && target!.Options.UseGcBarrier;

        string? provenance = null;
        string? dumpPath = null;
        long gcAtEmit = -1;
        ProvenanceState state = ProvenanceState.None;
        try
        {
            if (coherentCapture)
            {
                CoherentCaptureResult capture = target!.CaptureCoherentSnapshot(pid, CaptureTimeout);
                dumpPath = capture.DumpPath;
                provenance = capture.ProvenancePath;
                gcAtEmit = capture.GcCount;
                state = ProvenanceState.Exact;
            }
            else if (profiled)
            {
                if (correlationRequested)
                {
                    (provenance, gcAtEmit) = target!.CaptureCorrelation(pid, CaptureTimeout);
                }
                else
                {
                    provenance = target!.CaptureAllocations(pid, CaptureTimeout);
                }
            }

            if (profiled)
            {
                if (provenance is null)
                {
                    throw new DumpAnalysisException($"Could not capture allocations from process {pid}; no snapshot was created.");
                }
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

            if (correlationRequested && !coherentCapture)
            {
                state = CorrelationState(gcAtEmit, target!.GcCount(pid, DriftTimeout));
            }

            return new SnapshotCaptureResult(dumpPath, provenance, state);
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

    internal static ProvenanceState CorrelationState(long gcAtEmit, long gcAfterDump) =>
        gcAtEmit < 0 || gcAfterDump < 0
            ? ProvenanceState.Unverified
            : gcAfterDump == gcAtEmit
                ? ProvenanceState.Exact
                : ProvenanceState.Drifted;

    /// <summary>Reports capture or cataloguing failure without deleting completed artifacts.</summary>
    public static DumpAnalysisException Failure(int pid, Exception error, SnapshotCaptureResult? capture) =>
        Failure(pid, error, capture?.DumpPath, capture?.ProvenancePath);

    private static DumpAnalysisException Failure(int pid, Exception error, string? dumpPath, string? provenancePath)
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
