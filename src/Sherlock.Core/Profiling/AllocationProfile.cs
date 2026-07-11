using System.Collections.Generic;
using System.Linq;
using Sherlock.Core.Storage;

namespace Sherlock.Core.Profiling;

/// <summary>
/// One allocation call stack and what it allocated. <see cref="Frames"/> runs
/// root -> leaf (the allocating method last). <see cref="SurvivedBytes"/>/
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

/// <summary>Reads an allocation profile from a <c>.slab</c> container (the Allocations section + stack table).</summary>
public static class AllocationProfileReader
{
    public static AllocationProfile Read(string path)
    {
        using ContainerReader container = ContainerReader.Open(path);
        return From(new ProvenanceReader(container));
    }

    /// <summary>Materializes the profile from an already-open provenance reader.</summary>
    public static AllocationProfile From(ProvenanceReader reader)
    {
        var sites = new List<AllocationSite>();
        foreach (AllocationRecord rec in reader.Allocations)
        {
            string[] frames = reader.Stacks.FrameNames(rec.StackId); // root -> leaf
            sites.Add(new AllocationSite(
                frames, (long)rec.AllocBytes, (long)rec.AllocCount, (long)rec.SurvivedBytes, (long)rec.SurvivedCount));
        }
        return new AllocationProfile(sites);
    }
}
