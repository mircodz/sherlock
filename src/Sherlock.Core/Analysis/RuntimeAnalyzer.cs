using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>Lists runtime structure: loaded modules and GC heap segments.</summary>
public sealed class RuntimeAnalyzer(Snapshot snapshot)
{
    public IReadOnlyList<ModuleInfo> GetModules()
    {
        var modules = new List<ModuleInfo>();
        foreach (ClrModule module in snapshot.Runtime.EnumerateModules())
        {
            modules.Add(new ModuleInfo(
                Name: module.Name ?? "<dynamic>",
                ImageBase: module.ImageBase,
                Size: module.Size,
                IsDynamic: module.IsDynamic));
        }

        return modules
            .OrderBy(m => Path.GetFileName(m.Name), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<SegmentInfo> GetSegments()
    {
        var segments = new List<SegmentInfo>();
        foreach (ClrSegment segment in snapshot.Runtime.Heap.Segments)
        {
            segments.Add(new SegmentInfo(
                Start: segment.Start,
                End: segment.End,
                Length: segment.Length,
                Kind: segment.Kind.ToString()));
        }

        return segments
            .OrderBy(s => s.Kind, StringComparer.Ordinal)
            .ThenBy(s => s.Start)
            .ToList();
    }

    public HeapGenerations GetGenerations()
    {
        ulong gen0 = 0, gen1 = 0, gen2 = 0, large = 0, pinned = 0, frozen = 0;
        foreach (ClrSegment segment in snapshot.Runtime.Heap.Segments)
        {
            switch (segment.Kind)
            {
                // Without regions, one segment per heap holds gen0, gen1 and the newest part of gen2.
                case GCSegmentKind.Ephemeral:
                    gen0 += segment.Generation0.Length;
                    gen1 += segment.Generation1.Length;
                    gen2 += segment.Generation2.Length;
                    break;
                case GCSegmentKind.Generation0: gen0 += segment.Length; break;
                case GCSegmentKind.Generation1: gen1 += segment.Length; break;
                case GCSegmentKind.Generation2: gen2 += segment.Length; break;
                case GCSegmentKind.Large: large += segment.Length; break;
                case GCSegmentKind.Pinned: pinned += segment.Length; break;
                case GCSegmentKind.Frozen: frozen += segment.Length; break;
            }
        }
        return new HeapGenerations(gen0, gen1, gen2, large, pinned, frozen);
    }
}
