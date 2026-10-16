using System;
using System.Threading;
using Cellar.Widgets;
using Sherlock.Core;
using Sherlock.Core.Store;

namespace Sherlock.CLI.Tui;

/// <summary>A snapshot open in the explorer. Views build from it on background threads, and leaving a page only asks
/// them to stop, so closing the snapshot waits for the last one instead of freeing memory it still reads.</summary>
internal sealed class OpenedSnapshot(Snapshot snapshot, SnapshotEntry entry)
{
    private readonly Lock _gate = new();
    private int _readers;
    private bool _closed;

    public Snapshot Snapshot => snapshot;
    public SnapshotEntry Entry => entry;

    /// <summary>A view built in the background, showing progress until it's ready.</summary>
    public Widget Background(Func<Snapshot, CancellationToken, Widget> build) =>
        new AsyncContent(cancellation => Read(() => build(snapshot, cancellation)));

    /// <summary>Closes the snapshot now, or when the last background view reading it finishes.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            if (_readers > 0)
            {
                return;
            }
        }
        snapshot.Dispose();
    }

    private T Read<T>(Func<T> read)
    {
        lock (_gate)
        {
            // Only a view whose page is already gone starts after the close.
            if (_closed)
            {
                throw new OperationCanceledException();
            }
            _readers++;
        }
        try
        {
            return read();
        }
        finally
        {
            bool last;
            lock (_gate)
            {
                last = --_readers == 0 && _closed;
            }
            if (last)
            {
                snapshot.Dispose();
            }
        }
    }
}
