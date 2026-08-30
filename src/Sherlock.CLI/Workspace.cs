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

/// <summary>
/// The session library plus the one loaded snapshot that analysis commands operate on. Loading a
/// snapshot swaps the live <see cref="DumpSession"/> and disposes the previous one.
/// </summary>
public sealed class Workspace(SnapshotStore store) : IDisposable
{
    public SnapshotStore Store { get; } = store;

    private readonly List<ProcessSupervisor> _targets = [];
    private readonly Lock _captureGate = new();

    /// <summary>Processes launched with <c>run</c> during this session.</summary>
    public IReadOnlyList<ProcessSupervisor> Targets => _targets;

    public void AddTarget(ProcessSupervisor supervisor) => _targets.Add(supervisor);

    /// <summary>The currently-loaded snapshot (dump + provenance), or null if nothing is loaded.</summary>
    public Snapshot? Current { get; private set; }

    /// <summary>The catalog entry backing <see cref="Current"/>, if it came from the library.</summary>
    public SnapshotEntry? CurrentEntry { get; private set; }

    /// <summary>The session that owns <see cref="CurrentEntry"/>.</summary>
    public Session? CurrentSession { get; private set; }

    /// <summary>Short label for the prompt (snapshot id, or a file name for transient loads).</summary>
    public string? CurrentName { get; private set; }

    /// <summary>Loads a catalogued snapshot as the current target.</summary>
    public void Load(Session session, SnapshotEntry entry)
    {
        Swap(new Snapshot(DumpSession.Open(entry.Path), entry), session, entry, entry.Id);
    }

    /// <summary>Loads a dump file directly, without adding it to the library.</summary>
    public void LoadTransient(string path)
    {
        Swap(new Snapshot(DumpSession.Open(path)), session: null, entry: null, Path.GetFileName(path));
    }

    /// <summary>Imports crash dumps left by exited run-targets, each attached to its run's session.</summary>
    public IReadOnlyList<SnapshotEntry> PollExitedCrashDumps()
    {
        List<SnapshotEntry>? imported = null;
        foreach (ProcessSupervisor target in _targets)
        {
            string? path = target.TryPollRootCrashDump();
            if (path is null)
            {
                continue;
            }

            Session session = SessionFor(target, SessionKind.Crash);
            (imported ??= []).Add(Store.AddSnapshot(session, path, moveIntoStore: true,
                sourcePid: target.RootPid, sourceName: target.RootName, reason: "crash"));
        }
        return (IReadOnlyList<SnapshotEntry>?)imported ?? [];
    }

    /// <summary>Marks exit-time allocation profiles from exited <c>run --profile</c> targets.</summary>
    public IReadOnlyList<Session> PollExitedAllocationProfiles()
    {
        List<Session>? marked = null;
        foreach (ProcessSupervisor target in _targets)
        {
            if (target.SessionId is null || Store.GetSession(target.SessionId) is not { } session)
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

    /// <summary>For each probe that has signalled, dumps the firing process into its run's session, labelled with the probe.</summary>
    public IReadOnlyList<TriggeredCaptureResult> PollProbeSnapshots()
    {
        List<TriggeredCaptureResult>? captured = null;
        foreach (ProcessSupervisor target in _targets)
        {
            IReadOnlyList<(int Pid, string Name)> signals = target.TryPollProbeSignals();
            if (signals.Count == 0 || target.SessionId is null)
            {
                continue;
            }

            foreach ((int firingPid, string probe) in signals)
            {
                try
                {
                    // Capture the process that fired (a child under `dotnet run`, not the
                    // launcher), so a triggered snapshot carries provenance. Don't auto-load it.
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

    /// <summary>Collects a dump from a live process (sl-side WriteDump), catalogs it, and loads it.</summary>
    public SnapshotEntry Collect(int pid, DumpKind kind, bool load = true, string? provenance = null, bool correlated = false, string? reason = null)
    {
        string temp = DumpCollector.Collect(pid, kind, outputPath: null);
        return Ingest(pid, temp, load, provenance, correlated, reason);
    }

    /// <summary>Catalogs an already-written dump file under the run session this pid belongs to.</summary>
    public SnapshotEntry Ingest(int pid, string dumpPath, bool load = true, string? provenance = null, bool correlated = false, string? reason = null)
    {
        // Attribute the snapshot to the run that owns this pid (root or a live descendant), so a
        // child's snapshot lands in the run's workspace instead of a stray collect session.
        ProcessSupervisor? target = _targets.FirstOrDefault(t => t.SessionId is not null && Owns(t, pid));
        Session session = target?.SessionId is { } sid && Store.GetSession(sid) is { } s
            ? s
            : Store.BeginSession(SessionKind.Collect, NameOf(pid));

        SnapshotEntry entry = Store.AddSnapshot(session, dumpPath, moveIntoStore: true,
            sourcePid: pid, sourceName: NameOf(pid) ?? target?.RootName,
            provenanceSource: provenance, correlated: correlated, reason: reason);
        if (load)
        {
            Load(session, entry);
        }

        return entry;
    }

    /// <summary>
    /// Coherently snapshots a live process: for a profiled/correlated target it forces a GC and
    /// captures allocation state at the same instant as the dump, bundling it into the snapshot.
    /// </summary>
    public CaptureResult Capture(int pid, bool load = true, string? reason = null)
    {
        lock (_captureGate)
        {
            ProcessSupervisor? target = _targets.FirstOrDefault(t => !t.RootExited && Owns(t, pid));
            bool profiled = target?.ProfileOutPath is not null;
            bool correlationRequested = target is { HasCorrelation: true };

            string? provenance = null;
            string? dumpPath = null;
            long gcAtEmit = -1;
            try
            {
                if (profiled)
                {
                    if (correlationRequested)
                    {
                        // Unified provenance.slab (allocations + correlation) at this instant.
                        (provenance, gcAtEmit) =
                            target!.RequestCorrelationSnapshot(pid, CaptureTimeout);
                    }
                    else
                    {
                        provenance = target!.FlushAllocations(
                            pid, CaptureTimeout, throwOnError: true);
                    }

                    if (provenance is null)
                    {
                        throw new DumpAnalysisException(
                            $"Could not capture allocations from process {pid}; no snapshot was created.");
                    }
                    try
                    {
                        ProvenanceReader.ValidateFile(provenance);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new DumpAnalysisException(
                            $"The profiler produced invalid allocation data for process {pid}: {ex.Message}", ex);
                    }
                }

                dumpPath = DumpCollector.Collect(pid, DumpKind.Heap);

                ProvenanceState state = ProvenanceState.None;
                if (correlationRequested)
                {
                    long gcAfterDump = target!.GcCount(pid, DriftTimeout);
                    state = gcAtEmit < 0 || gcAfterDump < 0
                        ? ProvenanceState.Unverified
                        : gcAfterDump == gcAtEmit
                            ? ProvenanceState.Exact
                            : ProvenanceState.Drifted;
                }

                SnapshotEntry entry = Ingest(
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
                throw new DumpAnalysisException(
                    $"{ex.Message}{preserved}", ex);
            }
            catch (Exception ex) when (ex is not DumpAnalysisException)
            {
                throw new DumpAnalysisException(
                    $"Could not create snapshot for process {pid}: {ex.Message}" +
                    PreservedArtifacts(dumpPath, provenance), ex);
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

    /// <summary>Whether a run-target's process tree contains this pid (its root or a live descendant).</summary>
    private static bool Owns(ProcessSupervisor target, int pid) =>
        target.RootPid == pid || target.List().Any(p => p.Pid == pid);

    /// <summary>Finds (or lazily creates) the library session a run-target belongs to.</summary>
    private Session SessionFor(ProcessSupervisor target, SessionKind fallbackKind)
    {
        if (target.SessionId is { } sid && Store.GetSession(sid) is { } existing)
        {
            return existing;
        }

        return Store.BeginSession(fallbackKind, target.RootName);
    }

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
        foreach (ProcessSupervisor target in _targets)
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
