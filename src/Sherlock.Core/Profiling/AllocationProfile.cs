using System;
using System.Collections.Generic;
using System.Linq;
using Sherlock.Core.Storage;

namespace Sherlock.Core.Profiling;

/// <summary>
/// One allocation call stack and what it allocated. <see cref="Frames"/> runs root -> leaf (the
/// allocating method last). <see cref="SurvivedBytes"/>/<see cref="SurvivedCount"/> are the subset
/// that outlived their first GC.
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

/// <summary>Allocated and first-GC-surviving bytes for a type, plus its allocation-site count.</summary>
public sealed record AllocationTypeStat(
    string TypeName, long AllocBytes, long AllocCount, long SurvivedBytes, int SiteCount);

/// <summary>A leaf method's direct allocations and bytes allocated through its call stacks.</summary>
public sealed record AllocationMethodStat(string Method, long SelfBytes, long InclusiveBytes, long AllocCount);

/// <summary>A parsed allocation profile produced by the native profiler.</summary>
public sealed record AllocationProfile(IReadOnlyList<AllocationSite> Sites)
{
    public long TotalAllocBytes => Sites.Sum(s => s.AllocBytes);
    public long TotalSurvivedBytes => Sites.Sum(s => s.SurvivedBytes);

    /// <summary>True when the profile carries per-site allocated types (v2+ slabs).</summary>
    public bool HasTypes => Sites.Any(s => s.TypeName is not null);

    /// <summary>Allocating methods, largest self bytes first. Recursive frames count once per site
    /// toward inclusive bytes; sites without managed frames are excluded.</summary>
    public IReadOnlyList<AllocationMethodStat> HotMethods(int limit = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        if (limit == 0)
        {
            return [];
        }

        var self = new Dictionary<string, (long Bytes, long Count)>(StringComparer.Ordinal);
        var inclusive = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (AllocationSite site in Sites)
        {
            if (site.Frames.Count == 0)
            {
                continue;
            }

            string leaf = site.Frames[^1];
            (long Bytes, long Count) current = self.GetValueOrDefault(leaf);
            self[leaf] = (current.Bytes + site.AllocBytes, current.Count + site.AllocCount);
            foreach (string frame in site.Frames.Distinct(StringComparer.Ordinal))
            {
                inclusive[frame] = inclusive.GetValueOrDefault(frame) + site.AllocBytes;
            }
        }

        return self.OrderByDescending(pair => pair.Value.Bytes)
            .Take(limit)
            .Select(pair => new AllocationMethodStat(pair.Key, pair.Value.Bytes, inclusive[pair.Key], pair.Value.Count))
            .ToList();
    }

    /// <summary>Allocation totals grouped by allocated type, largest churn first. Empty for v1 slabs.</summary>
    public IReadOnlyList<AllocationTypeStat> ByType() =>
        Sites.Where(s => s.TypeName is not null)
            .GroupBy(s => s.TypeName!)
            .Select(g => new AllocationTypeStat(g.Key, g.Sum(s => s.AllocBytes), g.Sum(s => s.AllocCount), g.Sum(s => s.SurvivedBytes), g.Count()))
            .OrderByDescending(t => t.AllocBytes)
            .ToList();

    /// <summary>Sites allocating exactly <paramref name="typeName"/>.</summary>
    public AllocationProfile OfType(string typeName) =>
        new(Sites.Where(s => s.TypeName == typeName).ToList());

    /// <summary>Sites whose call stack contains <paramref name="method"/>.</summary>
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
        Column<AllocationRecord> allocations = reader.Allocations;
        var sites = new List<AllocationSite>(checked((int)allocations.Length));
        for (long i = 0; i < allocations.Length; i++)
        {
            AllocationRecord rec = allocations[i];
            string[] frames = reader.Stacks.FrameNames(rec.StackId); // root -> leaf
            string? typeName = hasType ? reader.Stacks.Frame(rec.TypeId) : null;
            sites.Add(new AllocationSite(
                frames, (long)rec.AllocBytes, (long)rec.AllocCount, (long)rec.SurvivedBytes, (long)rec.SurvivedCount, typeName));
        }
        return new AllocationProfile(sites);
    }
}
