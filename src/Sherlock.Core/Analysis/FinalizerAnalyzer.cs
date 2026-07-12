using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>
/// Summarizes objects still registered for finalization. A live finalizer registration means
/// <c>Dispose()</c> never ran (a proper Dispose calls <c>GC.SuppressFinalize</c>), so a large
/// population here is the classic "forgot to dispose" leak - and those objects survive an extra GC.
/// </summary>
public sealed class FinalizerAnalyzer(DumpSession session)
{
    public FinalizerReport Analyze(CancellationToken cancellation = default)
    {
        var byType = new Dictionary<string, (long Count, ulong Bytes)>(StringComparer.Ordinal);
        long total = 0;
        ulong totalBytes = 0;

        foreach (ClrObject obj in session.Runtime.Heap.EnumerateFinalizableObjects())
        {
            cancellation.ThrowIfCancellationRequested();
            if (obj.Type is not { } type)
            {
                continue;
            }

            string name = type.Name ?? "<unknown>";
            (long Count, ulong Bytes) cur = byType.GetValueOrDefault(name);
            byType[name] = (cur.Count + 1, cur.Bytes + obj.Size);
            total++;
            totalBytes += obj.Size;
        }

        List<FinalizableTypeStat> ranked = byType
            .Select(kv => new FinalizableTypeStat(kv.Key, kv.Value.Count, kv.Value.Bytes))
            .OrderByDescending(s => s.Count)
            .ToList();

        return new FinalizerReport(total, totalBytes, ranked);
    }
}
