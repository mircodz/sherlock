using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>Finds managed exception objects, both on threads and loose on the heap.</summary>
public sealed class ExceptionAnalyzer
{
    private readonly DumpSession _session;

    public ExceptionAnalyzer(DumpSession session) => _session = session;

    public IReadOnlyList<ExceptionInfo> FindExceptions(CancellationToken cancellationToken = default)
    {
        var byAddress = new Dictionary<ulong, ExceptionInfo>();

        // Exceptions currently in flight on a thread are the most interesting.
        foreach (ClrThread thread in _session.Runtime.Threads)
        {
            ClrException? current = thread.CurrentException;
            if (current is not null)
                byAddress[current.Address] = Describe(current, thread.ManagedThreadId);
        }

        // Plus any other exception objects still alive on the heap.
        foreach (ClrObject obj in _session.Runtime.Heap.EnumerateObjects())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!obj.IsException || byAddress.ContainsKey(obj.Address))
                continue;

            ClrException? ex = obj.AsException();
            if (ex is not null)
                byAddress[obj.Address] = Describe(ex, threadId: null);
        }

        return byAddress.Values
            .OrderByDescending(e => e.ThreadId.HasValue)
            .ThenBy(e => e.TypeName, StringComparer.Ordinal)
            .ToList();
    }

    private static ExceptionInfo Describe(ClrException ex, int? threadId) => new(
        Address: ex.Address,
        TypeName: ex.Type?.Name ?? "<unknown>",
        Message: ex.Message,
        StackFrameCount: ex.StackTrace.Count(),
        ThreadId: threadId);
}
