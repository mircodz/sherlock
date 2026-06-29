using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sherlock.Core.Profiling;

/// <summary>
/// One allocation call stack and what it allocated. <see cref="Frames"/> runs
/// root → leaf (the allocating method last). <see cref="SurvivedBytes"/>/
/// <see cref="SurvivedCount"/> are the subset that outlived their first GC.
/// </summary>
public sealed record AllocationSite(
    IReadOnlyList<string> Frames,
    long AllocBytes,
    long AllocCount,
    long SurvivedBytes,
    long SurvivedCount)
{
    /// <summary>The allocating method (leaf of the stack), or a placeholder.</summary>
    public string Method => Frames.Count > 0 ? Frames[^1] : "<no managed frame>";
}

/// <summary>A parsed allocation profile produced by the native profiler.</summary>
public sealed record AllocationProfile(IReadOnlyList<AllocationSite> Sites)
{
    public long TotalAllocBytes => Sites.Sum(s => s.AllocBytes);
    public long TotalSurvivedBytes => Sites.Sum(s => s.SurvivedBytes);
}

/// <summary>
/// Reads the profiler's folded-stack output (<c>allocations.tsv</c>):
/// <c>alloc_bytes\talloc_count\tsurvived_bytes\tsurvived_count\troot;…;leaf</c>,
/// with <c>#</c> comment lines. Also tolerates the older 3-column form
/// (<c>bytes\tcount\tstack</c>).
/// </summary>
public static class AllocationProfileReader
{
    public static AllocationProfile Read(string path)
    {
        var sites = new List<AllocationSite>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            string[] parts = line.Split('\t');
            if (parts.Length < 3)
                continue;

            string stack = parts[^1];
            string[] frames = stack.Split(';', System.StringSplitOptions.RemoveEmptyEntries);

            long allocBytes = ParseLong(parts[0]);
            long allocCount = ParseLong(parts[1]);
            long survivedBytes = parts.Length >= 5 ? ParseLong(parts[2]) : 0;
            long survivedCount = parts.Length >= 5 ? ParseLong(parts[3]) : 0;

            sites.Add(new AllocationSite(frames, allocBytes, allocCount, survivedBytes, survivedCount));
        }
        return new AllocationProfile(sites);
    }

    private static long ParseLong(string s) => long.TryParse(s, out long v) ? v : 0;
}
