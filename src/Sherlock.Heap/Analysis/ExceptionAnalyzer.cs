using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Heap.Analysis;

/// <summary>Finds managed exception objects, both on threads and loose on the heap. A parallel walk over the mapped dump
/// finds them, and ClrMD reads only those.</summary>
public sealed class ExceptionAnalyzer(HeapDump dump)
{
    public IReadOnlyList<ExceptionInfo> FindExceptions(CancellationToken cancellationToken = default)
    {
        var byAddress = new Dictionary<ulong, ExceptionInfo>();

        foreach (ClrThread thread in dump.Runtime.Threads)
        {
            ClrException? current = thread.CurrentException;
            if (current is not null)
            {
                byAddress[current.Address] = Describe(current, thread.ManagedThreadId);
            }
        }

        ClrHeap heap = dump.Runtime.Heap;
        foreach (ulong address in HeapWalk.Select(dump.GetDumpHeap(cancellationToken), type => type.IsException, static (_, _, _) => true, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byAddress.ContainsKey(address) && heap.GetObject(address).AsException() is { } ex)
            {
                byAddress[address] = Describe(ex, threadId: null);
            }
        }

        return byAddress.Values
            .OrderByDescending(e => e.ThreadId.HasValue)
            .ThenBy(e => e.TypeName, StringComparer.Ordinal)
            .ThenBy(e => e.Address)
            .ToList();
    }

    private static ExceptionInfo Describe(ClrException ex, int? threadId) => new(
        Address: ex.Address,
        TypeName: ex.Type?.Name ?? "<unknown>",
        Message: ex.Message,
        StackFrameCount: ex.StackTrace.Count(),
        ThreadId: threadId);
}
