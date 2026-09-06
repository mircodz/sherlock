using System;
using System.Collections.Generic;
using System.Threading;
using Sherlock.Core;
using Sherlock.Core.Store;

namespace Sherlock.Mcp;

/// <summary>Owns server-lifetime snapshots; serializes queries because ClrMD and analysis caches are not thread-safe.</summary>
public sealed class OpenSnapshots(SnapshotStore store) : IDisposable
{
    private readonly Dictionary<string, Snapshot> _cache = [];
    private readonly Lock _gate = new();

    public T Query<T>(string idOrLabel, Func<Snapshot, T> query)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(idOrLabel, out Snapshot? snapshot))
            {
                snapshot = store.Open(idOrLabel);
                _cache[idOrLabel] = snapshot;
            }
            return query(snapshot);
        }
    }

    public void Dispose()
    {
        foreach (Snapshot snapshot in _cache.Values)
        {
            snapshot.Dispose();
        }
    }
}
