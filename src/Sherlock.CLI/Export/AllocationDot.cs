using System;
using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core.Profiling;

namespace Sherlock.CLI.Export;

/// <summary>Call graph with inclusive-byte colors, self-byte sizes, and weighted caller-to-callee edges.</summary>
public static class AllocationDot
{
    // Allocations with no managed code on the stack (startup, statics, runtime helpers) get a node of their own, so
    // the graph accounts for every byte. Frames without a module identity equal only themselves.
    private static readonly MethodFrame NoManagedFrame = new(Guid.Empty, "", 0, "", "", "", "", "", "", "", "", null);

    public static string Write(AllocationProfile profile, double nodeFraction = 0.01, int maxNodes = 60)
    {
        long total = profile.Sites.Sum(s => s.AllocBytes);
        if (total <= 0)
        {
            return new DotGraph("allocations").Render();
        }

        var flat = new Dictionary<MethodFrame, long>();
        var cum = new Dictionary<MethodFrame, long>();
        var edges = new Dictionary<(MethodFrame From, MethodFrame To), long>();

        foreach (AllocationSite site in profile.Sites)
        {
            long bytes = site.AllocBytes;
            if (bytes <= 0)
            {
                continue;
            }

            IReadOnlyList<MethodFrame> frames = site.Frames.Count > 0 ? site.Frames : [NoManagedFrame];
            flat[frames[^1]] = flat.GetValueOrDefault(frames[^1]) + bytes;
            // A method and an edge count once per stack, however often recursion repeats them, so no weight
            // exceeds the total.
            foreach (MethodFrame frame in frames.Distinct())
            {
                cum[frame] = cum.GetValueOrDefault(frame) + bytes;
            }
            foreach ((MethodFrame, MethodFrame) edge in frames.Zip(frames.Skip(1)).Distinct())
            {
                edges[edge] = edges.GetValueOrDefault(edge) + bytes;
            }
        }

        // Prune nodes and their incident edges together.
        long cutoff = (long)(total * nodeFraction);
        List<MethodFrame> ranked = cum
            .Where(kv => kv.Value >= cutoff)
            .OrderByDescending(kv => kv.Value)
            .Take(maxNodes)
            .Select(kv => kv.Key)
            .ToList();
        // Node ids are ranks, so they can't collide.
        var kept = new Dictionary<MethodFrame, string>();
        for (int i = 0; i < ranked.Count; i++)
        {
            kept[ranked[i]] = $"n{i}";
        }

        double maxFlat = ranked.Count > 0 ? Math.Max(1, ranked.Max(m => flat.GetValueOrDefault(m))) : 1;

        var dot = new DotGraph("allocations");
        foreach (MethodFrame method in ranked)
        {
            long self = flat.GetValueOrDefault(method);
            long through = cum[method];
            dot.AddNode(kept[method], heat: (double)through / total, size: self / maxFlat, Label(method, self, through, total));
        }

        foreach (((MethodFrame from, MethodFrame to), long bytes) in edges.OrderByDescending(e => e.Value))
        {
            if (kept.TryGetValue(from, out string? fromId) && kept.TryGetValue(to, out string? toId))
            {
                dot.AddEdge(fromId, toId, (double)bytes / total, ByteSize.Format(bytes));
            }
        }

        return dot.Render();
    }

    private static string[] Label(MethodFrame method, long flat, long cum, long total)
    {
        string name = ReferenceEquals(method, NoManagedFrame) ? "<no managed frame>" : method.Label;
        if (flat == cum)
        {
            return [name, $"{ByteSize.Format(cum)} ({Counts.Percent(100.0 * cum / total)})"];
        }
        if (flat > 0)
        {
            return [name, $"flat {ByteSize.Format(flat)} ({Counts.Percent(100.0 * flat / total)})", $"cum {ByteSize.Format(cum)} ({Counts.Percent(100.0 * cum / total)})"];
        }
        return [name, $"cum {ByteSize.Format(cum)} ({Counts.Percent(100.0 * cum / total)})"];
    }
}
