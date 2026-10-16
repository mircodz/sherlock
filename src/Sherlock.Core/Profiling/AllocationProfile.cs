using System;
using System.Collections.Generic;
using System.Linq;
using Sherlock.Core.Storage;

namespace Sherlock.Core.Profiling;

/// <summary>
/// One allocation call stack and what it allocated. <see cref="Frames"/> runs root -> leaf (the
/// allocating method last) and is empty when no managed code was on the stack.
/// <see cref="SurvivedBytes"/>/<see cref="SurvivedCount"/> are the subset that outlived their first GC.
/// </summary>
public sealed record AllocationSite(
    IReadOnlyList<MethodFrame> Frames,
    string TypeName,
    long AllocBytes,
    long AllocCount,
    long SurvivedBytes,
    long SurvivedCount);

/// <summary>Allocated and first-GC-surviving bytes for a type, plus its allocation-site count.</summary>
public sealed record AllocationTypeStat(
    string TypeName, long AllocBytes, long AllocCount, long SurvivedBytes, int SiteCount);

/// <summary>A leaf method's direct allocations and bytes allocated through its call stacks.</summary>
public sealed record AllocationMethodStat(MethodFrame Method, long SelfBytes, long InclusiveBytes, long AllocCount);

/// <summary>A parsed allocation profile produced by the native profiler.</summary>
public sealed record AllocationProfile(IReadOnlyList<AllocationSite> Sites)
{
    public long TotalAllocBytes => Sites.Sum(s => s.AllocBytes);
    public long TotalSurvivedBytes => Sites.Sum(s => s.SurvivedBytes);

    /// <summary>Allocating methods, largest self bytes first. Recursive frames count once per site
    /// toward inclusive bytes; sites without managed frames are excluded.</summary>
    public IReadOnlyList<AllocationMethodStat> HotMethods(int limit = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        if (limit == 0)
        {
            return [];
        }

        var self = new Dictionary<MethodFrame, (long Bytes, long Count)>();
        var inclusive = new Dictionary<MethodFrame, long>();
        foreach (AllocationSite site in Sites)
        {
            if (site.Frames.Count == 0)
            {
                continue;
            }

            MethodFrame leaf = site.Frames[^1];
            (long Bytes, long Count) current = self.GetValueOrDefault(leaf);
            self[leaf] = (current.Bytes + site.AllocBytes, current.Count + site.AllocCount);
            foreach (MethodFrame frame in site.Frames.Distinct())
            {
                inclusive[frame] = inclusive.GetValueOrDefault(frame) + site.AllocBytes;
            }
        }

        return self.OrderByDescending(pair => pair.Value.Bytes)
            .Take(limit)
            .Select(pair => new AllocationMethodStat(pair.Key, pair.Value.Bytes, inclusive[pair.Key], pair.Value.Count))
            .ToList();
    }

    /// <summary>Allocation totals grouped by allocated type, largest churn first.</summary>
    public IReadOnlyList<AllocationTypeStat> ByType() =>
        Sites.GroupBy(s => s.TypeName, StringComparer.Ordinal)
            .Select(g => new AllocationTypeStat(g.Key, g.Sum(s => s.AllocBytes), g.Sum(s => s.AllocCount), g.Sum(s => s.SurvivedBytes), g.Count()))
            .OrderByDescending(t => t.AllocBytes)
            .ToList();

    /// <summary>Sites allocating exactly <paramref name="typeName"/>.</summary>
    public AllocationProfile OfType(string typeName) =>
        new(Sites.Where(s => s.TypeName == typeName).ToList());

    /// <summary>Sites whose call stack contains any of <paramref name="methods"/>.</summary>
    public AllocationProfile Through(IReadOnlyCollection<MethodFrame> methods)
    {
        HashSet<MethodFrame> set = [.. methods];
        return new(Sites.Where(s => s.Frames.Any(set.Contains)).ToList());
    }

    /// <summary>The methods on any stack that <paramref name="query"/> names (see <see cref="MethodFrame.Matches"/>). A query
    /// that is some method's label selects only the methods with that label, so a label always selects what it shows.</summary>
    public IReadOnlySet<MethodFrame> MethodsMatching(string query)
    {
        HashSet<MethodFrame> methods = Sites.SelectMany(s => s.Frames).ToHashSet();
        HashSet<MethodFrame> labelled = methods.Where(method => method.Label == query).ToHashSet();
        return labelled.Count > 0 ? labelled : methods.Where(method => method.Matches(query)).ToHashSet();
    }
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
        Column<AllocationRecord> allocations = reader.Allocations;
        var sites = new List<AllocationSite>(checked((int)allocations.Length));
        for (long i = 0; i < allocations.Length; i++)
        {
            AllocationRecord rec = allocations[i];
            sites.Add(new AllocationSite(reader.Symbols.Stack(rec.StackId), reader.Symbols.Types[(int)rec.TypeId],
                (long)rec.AllocBytes, (long)rec.AllocCount, (long)rec.SurvivedBytes, (long)rec.SurvivedCount));
        }
        return new AllocationProfile(sites);
    }
}
