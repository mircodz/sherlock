using System;
using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core.Profiling;

namespace Sherlock.CLI.Export;

/// <summary>
/// The allocation profile as a pprof-style call graph: methods are boxes sized/coloured by the bytes
/// flowing through them (flat = allocated directly, cum = including callees), edges are caller->callee
/// weighted by the bytes on that path. Same model as <c>go tool pprof -web</c>.
/// </summary>
public static class AllocationDot
{
    public static string Write(AllocationProfile profile, double nodeFraction = 0.01, int maxNodes = 60)
    {
        long total = profile.Sites.Sum(s => s.AllocBytes);
        if (total <= 0)
        {
            return new DotGraph("allocations").Render();
        }

        var flat = new Dictionary<string, long>(StringComparer.Ordinal);
        var cum = new Dictionary<string, long>(StringComparer.Ordinal);
        var edges = new Dictionary<(string From, string To), long>();

        foreach (AllocationSite site in profile.Sites)
        {
            long bytes = site.AllocBytes;
            if (bytes <= 0 || site.Frames.Count == 0)
            {
                continue;
            }

            flat[site.Frames[^1]] = flat.GetValueOrDefault(site.Frames[^1]) + bytes;
            foreach (string frame in site.Frames.Distinct()) // cum counts a method once per stack
            {
                cum[frame] = cum.GetValueOrDefault(frame) + bytes;
            }
            for (int i = 0; i + 1 < site.Frames.Count; i++)
            {
                (string, string) edge = (site.Frames[i], site.Frames[i + 1]);
                edges[edge] = edges.GetValueOrDefault(edge) + bytes;
            }
        }

        // Keep the heaviest methods above the cutoff; drop edges whose endpoints didn't survive.
        long cutoff = (long)(total * nodeFraction);
        HashSet<string> kept = cum
            .Where(kv => kv.Value >= cutoff)
            .OrderByDescending(kv => kv.Value)
            .Take(maxNodes)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);

        var dot = new DotGraph("allocations");
        foreach (string method in kept.OrderByDescending(m => cum[m]))
        {
            double weight = (double)cum[method] / total;
            long self = flat.GetValueOrDefault(method);
            if (self > 0)
            {
                dot.AddNode(Id(method), weight, ShortMethod(method),
                    $"cum {ByteSize.Format(cum[method])} ({100 * weight:0.0}%)",
                    $"flat {ByteSize.Format(self)} ({100.0 * self / total:0.0}%)");
            }
            else
            {
                dot.AddNode(Id(method), weight, ShortMethod(method),
                    $"cum {ByteSize.Format(cum[method])} ({100 * weight:0.0}%)");
            }
        }

        foreach (((string from, string to), long bytes) in edges.OrderByDescending(e => e.Value))
        {
            if (kept.Contains(from) && kept.Contains(to))
            {
                dot.AddEdge(Id(from), Id(to), (double)bytes / total, ByteSize.Format(bytes));
            }
        }

        return dot.Render();
    }

    /// <summary>A stable, DOT-safe node id from a method name.</summary>
    private static string Id(string method) => $"n{(uint)StringComparer.Ordinal.GetHashCode(method):x}";

    /// <summary>The last <c>Type.Method</c> of a fully-qualified frame.</summary>
    private static string ShortMethod(string frame)
    {
        string[] parts = frame.Split('.');
        return parts.Length <= 2 ? frame : string.Join('.', parts[^2..]);
    }
}
