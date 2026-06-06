using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>Lists runtime structure: loaded modules and GC heap segments.</summary>
public sealed class RuntimeAnalyzer(DumpSession session)
{
    public IReadOnlyList<ModuleInfo> GetModules()
    {
        var modules = new List<ModuleInfo>();
        foreach (ClrModule module in session.Runtime.EnumerateModules())
        {
            modules.Add(new ModuleInfo(
                Name: module.Name ?? "<dynamic>",
                ImageBase: module.ImageBase,
                Size: module.Size,
                IsDynamic: module.IsDynamic));
        }
        return modules.OrderBy(m => Path.GetFileName(m.Name), StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<SegmentInfo> GetSegments()
    {
        var segments = new List<SegmentInfo>();
        foreach (ClrSegment segment in session.Runtime.Heap.Segments)
        {
            segments.Add(new SegmentInfo(
                Start: segment.Start,
                End: segment.End,
                Length: segment.Length,
                Kind: segment.Kind.ToString()));
        }
        return segments.OrderBy(s => s.Kind, StringComparer.Ordinal).ThenBy(s => s.Start).ToList();
    }
}
