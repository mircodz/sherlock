using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Sherlock.Core;
using Sherlock.Core.Collection;
using Sherlock.Core.Profiling;
using Sherlock.Core.Store;

namespace Sherlock.CLI;

/// <summary>Owns active runs, the snapshot library, and the loaded snapshot.</summary>
public sealed class Workspace(SnapshotStore store) : IDisposable
{
    public SnapshotStore Store { get; } = store;

    private readonly List<RunTarget> _targets = [];
    private readonly Dictionary<RunTarget, string> _targetSessions = [];
    private readonly Lock _captureGate = new();

    public IReadOnlyList<RunTarget> Targets => _targets;

    public void AddTarget(RunTarget target, Session session)
    {
        _targets.Add(target);
        _targetSessions[target] = session.Id;
    }

    public RunTarget? FindTarget(Session? session) =>
        session is null
            ? null
            : _targets.LastOrDefault(
                target => _targetSessions.GetValueOrDefault(target) == session.Id);

    public Snapshot? Current { get; private set; }

    public SnapshotEntry? CurrentEntry { get; private set; }

    public Session? CurrentSession { get; private set; }

    public string? CurrentName { get; private set; }

    /// <summary>Loads a catalogued snapshot as the current target.</summary>
    public void Load(Session session, SnapshotEntry entry)
    {
        Swap(Store.Open(entry.Id), session, entry, entry.Id);
    }

    /// <summary>Loads a dump file directly, without adding it to the library.</summary>
    public void LoadTransient(string path)
    {
        Swap(Snapshot.Open(path), session: null, entry: null, Path.GetFileName(path));
    }

    /// <summary>Marks exit-time allocation profiles from exited <c>run --profile</c> targets.</summary>
    public IReadOnlyList<Session> PollExitedAllocationProfiles()
    {
        List<Session>? marked = null;
        foreach (RunTarget target in _targets)
        {
            if (FindSession(target) is not { } session)
            {
                continue;
            }

            IReadOnlyList<(int Pid, string Path)> profiles = target.PollAllocationProfiles();
            if (profiles.Count == 0)
            {
                continue;
            }

            foreach ((int pid, string path) in profiles)
            {
                Store.MarkAllocations(session, pid, target.NameFor(pid) ?? NameOf(pid), path);
            }
            (marked ??= []).Add(session);
        }

        return (IReadOnlyList<Session>?)marked ?? [];
    }

    public IReadOnlyList<TriggeredCaptureResult> PollTriggeredSnapshots()
    {
        List<TriggeredCaptureResult>? captured = null;
        foreach (RunTarget target in _targets)
        {
            IReadOnlyList<(int Pid, string Name)> signals = target.PollTriggers();
            if (signals.Count == 0 || FindSession(target) is null)
            {
                continue;
            }

            foreach ((int firingPid, string probe) in signals)
            {
                try
                {
                    SnapshotEntry entry = Capture(firingPid, load: false, reason: probe).Entry;
                    (captured ??= []).Add(new TriggeredCaptureResult(probe, entry, null));
                }
                catch (Exception ex)
                {
                    (captured ??= []).Add(new TriggeredCaptureResult(probe, null, ex.Message));
                }
            }
        }

        return (IReadOnlyList<TriggeredCaptureResult>?)captured ?? [];
    }

    private SnapshotEntry SaveSnapshot(
        int pid,
        string dumpPath,
        bool load,
        string? provenance,
        bool correlated,
        string? reason)
    {
        RunTarget? target = _targets.FirstOrDefault(t => Owns(t, pid));
        Session session = target is not null && FindSession(target) is { } existing
            ? existing
            : Store.BeginSession(SessionKind.Collect, NameOf(pid));

        SnapshotEntry entry = Store.AddSnapshot(session, dumpPath, moveIntoStore: true,
            sourcePid: pid, sourceName: NameOf(pid) ?? target?.Name,
            provenanceSource: provenance, correlated: correlated, reason: reason);
        if (load)
        {
            Load(session, entry);
        }

        return entry;
    }

    /// <summary>Captures a heap and, when profiled, cumulative allocations.</summary>
    public CaptureResult Capture(int pid, bool load = true, string? reason = null)
    {
        lock (_captureGate)
        {
            RunTarget? target = _targets.FirstOrDefault(t => !t.HasExited && Owns(t, pid));
            bool profiled = target?.AllocationPath is not null;
            bool correlationRequested = target is { HasCorrelation: true };
            bool coherentCapture = correlationRequested && target!.Options.ExperimentalGcBarrier;

            string? provenance = null;
            string? dumpPath = null;
            long gcAtEmit = -1;
            ProvenanceState state = ProvenanceState.None;
            try
            {
                if (coherentCapture)
                {
                    CoherentCaptureResult capture = target!.CaptureCoherentSnapshot(pid, CaptureTimeout);
                    dumpPath = capture.DumpPath;
                    provenance = capture.ProvenancePath;
                    gcAtEmit = capture.GcCount;
                    state = ProvenanceState.Exact;
                }
                else if (profiled)
                {
                    if (correlationRequested)
                    {
                        (provenance, gcAtEmit) = target!.CaptureCorrelation(pid, CaptureTimeout);
                    }
                    else
                    {
                        provenance = target!.CaptureAllocations(pid, CaptureTimeout);
                    }

                }

                if (profiled)
                {
                    if (provenance is null)
                    {
                        throw new DumpAnalysisException($"Could not capture allocations from process {pid}; no snapshot was created.");
                    }
                    try
                    {
                        ProvenanceReader.ValidateFile(provenance);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new DumpAnalysisException($"The profiler produced invalid allocation data for process {pid}: {ex.Message}", ex);
                    }
                }

                dumpPath ??= DumpCollector.Collect(pid, DumpKind.Heap);

                if (correlationRequested && !coherentCapture)
                {
                    long gcAfterDump = target!.GcCount(pid, DriftTimeout);
                    state = gcAtEmit < 0 || gcAfterDump < 0
                        ? ProvenanceState.Unverified
                        : gcAfterDump == gcAtEmit
                            ? ProvenanceState.Exact
                            : ProvenanceState.Drifted;
                }

                SnapshotEntry entry = SaveSnapshot(
                    pid, dumpPath, load, provenance,
                    correlated: state == ProvenanceState.Exact, reason);
                dumpPath = null; // moved into the snapshot bundle
                provenance = null; // copied into the bundle and removed by the store
                return new CaptureResult(entry, state);
            }
            catch (DumpAnalysisException ex)
            {
                string preserved = PreservedArtifacts(dumpPath, provenance);
                if (preserved.Length == 0)
                {
                    throw;
                }
                throw new DumpAnalysisException($"{ex.Message}{preserved}", ex);
            }
            catch (Exception ex) when (ex is not DumpAnalysisException)
            {
                throw new DumpAnalysisException($"Could not create snapshot for process {pid}: {ex.Message}{PreservedArtifacts(dumpPath, provenance)}", ex);
            }
        }
    }

    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DriftTimeout = TimeSpan.FromSeconds(3);

    private static string PreservedArtifacts(string? dumpPath, string? provenancePath)
    {
        var paths = new List<string>(2);
        if (dumpPath is not null && File.Exists(dumpPath))
        {
            paths.Add($"heap dump '{dumpPath}'");
        }
        if (provenancePath is not null && File.Exists(provenancePath))
        {
            paths.Add($"allocation data '{provenancePath}'");
        }
        return paths.Count == 0
            ? string.Empty
            : $" Preserved {string.Join(" and ", paths)}.";
    }

    private static bool Owns(RunTarget target, int pid) =>
        target.Pid == pid || target.Processes().Any(p => p.Pid == pid);

    private Session? FindSession(RunTarget target) =>
        _targetSessions.TryGetValue(target, out string? id)
            ? Store.GetSession(id)
            : null;

    private static string? NameOf(int pid)
    {
        try
        {
            return System.Diagnostics.Process.GetProcessById(pid).ProcessName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Closes the current snapshot, leaving nothing loaded.</summary>
    public void Unload()
    {
        Current?.Dispose();
        Current = null;
        CurrentEntry = null;
        CurrentSession = null;
        CurrentName = null;
    }

    private void Swap(Snapshot snapshot, Session? session, SnapshotEntry? entry, string name)
    {
        Current?.Dispose();
        Current = snapshot;
        CurrentSession = session;
        CurrentEntry = entry;
        CurrentName = name;
    }

    public void Dispose()
    {
        Current?.Dispose();
        foreach (RunTarget target in _targets)
        {
            target.Dispose(); // leaves processes running; just releases handles
        }
    }
}

/// <summary>Whether a snapshot carries allocation provenance, and if the address join is trustworthy.</summary>
public enum ProvenanceState
{
    None,
    Exact,
    Drifted,
    Unverified,
}

/// <summary>The outcome of <see cref="Workspace.Capture"/>: the new snapshot and its provenance state.</summary>
public sealed record CaptureResult(SnapshotEntry Entry, ProvenanceState Provenance);

public sealed record TriggeredCaptureResult(string Probe, SnapshotEntry? Entry, string? Error);
