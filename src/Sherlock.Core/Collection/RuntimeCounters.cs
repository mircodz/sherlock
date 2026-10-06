using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace Sherlock.Core.Collection;

/// <summary>Managed heap metrics. Generation sizes are as of the last GC; <paramref name="Collections"/> counts GCs
/// since monitoring started; <paramref name="At"/> is when the heap size arrived.</summary>
public sealed record HeapStats(long Total, long Gen0, long Gen1, long Gen2, long Loh, long Poh, long Collections, DateTimeOffset At);

/// <summary>Streams the runtime's System.Runtime counters over EventPipe. Works for any .NET process, profiled or not.</summary>
public sealed class RuntimeCounters : IDisposable
{
    private readonly EventPipeSession? _session;
    private readonly Lock _lock = new();
    private double _heapMegabytes = -1;
    private long _gen0, _gen1, _gen2, _loh, _poh, _collections;
    private DateTimeOffset _at;
    private int _disposed;

    public int Pid { get; }

    internal RuntimeCounters(int pid, EventPipeSession? session = null)
    {
        Pid = pid;
        _session = session;
    }

    /// <exception cref="DumpAnalysisException">The process has no reachable diagnostics port.</exception>
    public static RuntimeCounters Start(int pid, TimeSpan interval)
    {
        EventPipeSession session;
        try
        {
            var provider = new EventPipeProvider("System.Runtime", EventLevel.Informational, 0,
                new Dictionary<string, string> { ["EventCounterIntervalSec"] = interval.TotalSeconds.ToString(CultureInfo.InvariantCulture) });
            session = new DiagnosticsClient(pid).StartEventPipeSession([provider], requestRundown: false);
        }
        catch (Exception ex)
        {
            throw new DumpAnalysisException($"Runtime counters are unavailable for process {pid}: {ex.Message}", ex);
        }

        var counters = new RuntimeCounters(pid, session);
        _ = Task.Run(counters.Read);
        return counters;
    }

    /// <summary>The latest metrics, or null before the first counter interval arrives.</summary>
    public HeapStats? Latest
    {
        get
        {
            lock (_lock)
            {
                return _heapMegabytes < 0
                    ? null
                    : new HeapStats((long)(_heapMegabytes * 1_000_000), _gen0, _gen1, _gen2, _loh, _poh, _collections, _at);
            }
        }
    }

    private void Read()
    {
        try
        {
            using var source = new EventPipeEventSource(_session!.EventStream);
            source.Dynamic.All += e =>
            {
                if (e.EventName == "EventCounters" &&
                    e.PayloadValue(0) is IDictionary<string, object> envelope &&
                    envelope.TryGetValue("Payload", out object? payload) &&
                    payload is IDictionary<string, object> counter)
                {
                    Apply(counter, DateTimeOffset.Now);
                }
            };
            source.Process();
        }
        catch (Exception)
        {
            // The stream ends abruptly when the target exits or the session is stopped.
        }
    }

    internal void Apply(IDictionary<string, object> counter, DateTimeOffset at)
    {
        if (!counter.TryGetValue("Name", out object? nameValue) || nameValue is not string name ||
            !counter.TryGetValue(name == "gen-0-gc-count" ? "Increment" : "Mean", out object? raw) ||
            raw is not IConvertible convertible)
        {
            return;
        }

        double value = convertible.ToDouble(CultureInfo.InvariantCulture);
        lock (_lock)
        {
            switch (name)
            {
                case "gc-heap-size": // GC.GetTotalMemory in decimal megabytes
                    _heapMegabytes = value;
                    _at = at;
                    break;
                case "gen-0-size": _gen0 = (long)value; break;
                case "gen-1-size": _gen1 = (long)value; break;
                case "gen-2-size": _gen2 = (long)value; break;
                case "loh-size": _loh = (long)value; break;
                case "poh-size": _poh = (long)value; break;
                // Every GC collects gen0; the runtime primes the counter when the session starts.
                case "gen-0-gc-count": _collections += (long)value; break;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1 || _session is not { } session)
        {
            return;
        }
        // Stopping waits on the target's diagnostics thread, which is frozen while a dump is written.
        _ = Task.Run(() =>
        {
            try
            {
                session.Stop();
            }
            catch (Exception)
            {
                // The target may already be gone.
            }
            session.Dispose();
        });
    }
}
