using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Heap.Analysis;

/// <summary>Finds large delegate invocation lists and the subscriber types retained by their targets.</summary>
public sealed unsafe class EventHandlerAnalyzer(HeapDump dump)
{
    public IReadOnlyList<EventSubscription> Analyze(int minSubscribers = 16, int limit = 25, CancellationToken cancellation = default)
    {
        var results = new List<EventSubscription>();

        // The walk keeps delegates whose invocation list is at least that long, reading the list's length in place.
        ulong[] candidates = HeapWalk.Select(dump.GetDumpHeap(cancellation), type => type.InvocationList != 0, (address, type, cursor) =>
        {
            byte* field = cursor.At(address + (ulong)type.InvocationList, sizeof(ulong));
            ulong list = field == null ? 0 : *(ulong*)field;
            byte* length = list == 0 ? null : cursor.At(list + sizeof(ulong), sizeof(uint));
            return length != null && *(uint*)length >= (uint)minSubscribers;
        }, cancellation);

        ClrHeap heap = dump.Runtime.Heap;
        foreach (ulong address in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            ClrObject obj = heap.GetObject(address);
            if (obj.Type is not { } type)
            {
                continue;
            }

            ClrObject list = obj.ReadObjectField("_invocationList");
            if (!list.IsValid || !list.IsArray)
            {
                continue;
            }

            ClrArray invocation = list.AsArray();
            if (invocation.Length < minSubscribers)
            {
                continue;
            }

            // Invocation arrays can have spare capacity; trailing nulls are not subscribers.
            var targets = new Dictionary<string, int>(StringComparer.Ordinal);
            int subscribers = 0;
            for (int i = 0; i < invocation.Length; i++)
            {
                ClrObject handler = invocation.GetObjectValue(i);
                if (!handler.IsValid)
                {
                    continue;
                }

                subscribers++;
                ClrObject target = handler.ReadObjectField("_target");
                string name = target.IsValid && target.Type is { } t ? t.Name ?? "<unknown>" : "<static>";
                targets[name] = targets.GetValueOrDefault(name) + 1;
            }

            if (subscribers < minSubscribers)
            {
                continue;
            }

            List<HandlerTarget> byTarget = targets
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new HandlerTarget(kv.Key, kv.Value))
                .ToList();
            results.Add(new EventSubscription(obj.Address, type.Name ?? "<delegate>", subscribers, byTarget));
        }

        return results.OrderByDescending(r => r.SubscriberCount).ThenBy(r => r.DelegateAddress).Take(limit).ToList();
    }
}
