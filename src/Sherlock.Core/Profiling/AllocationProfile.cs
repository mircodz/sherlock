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
    long SurvivedCount,
    string? TypeName = null)
{
    /// <summary>The allocating method (leaf of the stack), or a placeholder.</summary>
    public string Method => Frames.Count > 0 ? Frames[^1] : "<no managed frame>";
}

/// <summary>What one allocated type accounts for across a profile: churn (allocated) and what stuck
/// (survived), plus how many distinct call sites produce it. A type from a single site is easy to
/// reason about; a survivor type from one hot site is a leak's smoking gun.</summary>
public sealed record AllocationTypeStat(
    string TypeName, long AllocBytes, long AllocCount, long SurvivedBytes, int SiteCount);

/// <summary>A parsed allocation profile produced by the native profiler.</summary>
public sealed record AllocationProfile(IReadOnlyList<AllocationSite> Sites)
{
    public long TotalAllocBytes => Sites.Sum(s => s.AllocBytes);
    public long TotalSurvivedBytes => Sites.Sum(s => s.SurvivedBytes);

    /// <summary>True when the profile carries per-site allocated types (v2+ slabs).</summary>
    public bool HasTypes => Sites.Any(s => s.TypeName is not null);

    /// <summary>Allocation totals grouped by allocated type, largest churn first. Empty for v1 slabs.</summary>
    public IReadOnlyList<AllocationTypeStat> ByType() =>
        Sites.Where(s => s.TypeName is not null)
            .GroupBy(s => s.TypeName!)
            .Select(g => new AllocationTypeStat(
                g.Key, g.Sum(s => s.AllocBytes), g.Sum(s => s.AllocCount), g.Sum(s => s.SurvivedBytes), g.Count()))
            .OrderByDescending(t => t.AllocBytes)
            .ToList();

    /// <summary>The subset of the profile that allocated <paramref name="typeName"/> — feed it to
    /// <see cref="AllocationTreeNode.Build"/> for that type's allocation call tree.</summary>
    public AllocationProfile OfType(string typeName) =>
        new(Sites.Where(s => s.TypeName == typeName).ToList());

    /// <summary>The subset of sites whose call stack passes through <paramref name="method"/> — its
    /// inclusive allocation. <c>.ByType()</c> on this is what a method allocates, by type; the total
    /// is its inclusive bytes.</summary>
    public AllocationProfile Through(string method) =>
        new(Sites.Where(s => s.Frames.Contains(method)).ToList());
}

/// <summary>Reads an allocation profile from a <c>.slab</c> container (the Allocations section + stack table).</summary>
public static class AllocationProfileReader
{
    public static AllocationProfile Read(string path)
    {
        using SlabFile slab = SlabFile.Open(path);
        return From(new ProvenanceReader(slab));
    }

    /// <summary>Materializes the profile from an already-open provenance reader.</summary>
    public static AllocationProfile From(ProvenanceReader reader)
    {
        bool hasType = reader.AllocationsVersion >= 2; // v1 slabs have no per-record type
        Column<AllocationRecord> allocs = reader.Allocations;
        var sites = new List<AllocationSite>(checked((int)allocs.Length));
        for (long i = 0; i < allocs.Length; i++)
        {
            AllocationRecord rec = allocs[i];
            string[] frames = reader.Stacks.FrameNames(rec.StackId); // root -> leaf
            string? typeName = hasType ? reader.Stacks.Frame(rec.TypeId) : null;
            sites.Add(new AllocationSite(
                frames, (long)rec.AllocBytes, (long)rec.AllocCount, (long)rec.SurvivedBytes, (long)rec.SurvivedCount, typeName));
        }
        return new AllocationProfile(sites);
    }
}
