using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>
/// Finds delegates whose invocation list has grown large - the classic event-handler leak, where a
/// long-lived publisher keeps every subscriber alive because they never unsubscribed (<c>-=</c>).
/// The subscriber (a delegate's <c>_target</c>) can't be collected while the event holds it.
/// </summary>
public sealed class EventHandlerAnalyzer(DumpSession session)
{
    public IReadOnlyList<EventSubscription> Analyze(int minSubscribers = 16, int limit = 25, CancellationToken cancellation = default)
    {
        var results = new List<EventSubscription>();

        foreach (ClrObject obj in session.Runtime.Heap.EnumerateObjects())
        {
            cancellation.ThrowIfCancellationRequested();
            if (obj.Type is not { } type || !IsDelegate(type))
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

            // The array is often over-allocated with trailing nulls, so count real handlers and
            // tally their target types (the subscriber instances the event pins alive).
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

        return results.OrderByDescending(r => r.SubscriberCount).Take(limit).ToList();
    }

    private static bool IsDelegate(ClrType type)
    {
        for (ClrType? t = type; t is not null; t = t.BaseType)
        {
            if (t.Name is "System.MulticastDelegate" or "System.Delegate")
            {
                return true;
            }
        }
        return false;
    }
}
