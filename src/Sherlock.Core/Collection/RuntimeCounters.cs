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

/// <summary>A metrics reading: <paramref name="Heap"/> is null until the first counter interval arrives, or when
/// <paramref name="Error"/> explains why the counters are unavailable.</summary>
public sealed record RuntimeMetrics(HeapStats? Heap, string? Error);

/// <summary>Streams the runtime's System.Runtime counters over EventPipe. Works for any .NET process, profiled or not.</summary>
public sealed class RuntimeCounters : IDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
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

    /// <summary>Starting or stopping a session runs managed code in the target, so it waits while the target's GC
    /// is suspended. Never call this, or <see cref="Dispose"/>, while a coherent capture holds the process.</summary>
    /// <exception cref="DumpAnalysisException">The process has no reachable diagnostics port or did not respond.</exception>
    public static RuntimeCounters Start(int pid, TimeSpan interval)
    {
        EventPipeSession session;
        try
        {
            var provider = new EventPipeProvider("System.Runtime", EventLevel.Informational, 0,
                new Dictionary<string, string> { ["EventCounterIntervalSec"] = interval.TotalSeconds.ToString(CultureInfo.InvariantCulture) });
            using var timeout = new CancellationTokenSource(CommandTimeout);
            // Counters need little buffering, and the target's memory is what is being measured.
            session = new DiagnosticsClient(pid)
                .StartEventPipeSessionAsync([provider], requestRundown: false, circularBufferMB: 4, timeout.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex)
        {
            throw new DumpAnalysisException(
                $"Runtime counters are unavailable for process {pid}: it did not respond within {CommandTimeout.TotalSeconds:0} seconds.", ex);
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
        try
        {
            using var timeout = new CancellationTokenSource(CommandTimeout);
            session.StopAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // The target is gone or unresponsive; closing the stream below also ends the session.
        }
        session.Dispose();
    }
}
