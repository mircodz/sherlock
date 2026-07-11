using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sherlock.Core;
using Sherlock.Core.Collection;
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
    public IReadOnlyList<(SnapshotEntry Entry, string Probe)> PollProbeSnapshots()
    {
        List<(SnapshotEntry, string)>? captured = null;
        foreach (ProcessSupervisor target in _targets)
        {
            IReadOnlyList<(int Pid, string Name)> signals = target.TryPollProbeSignals();
            if (signals.Count == 0 || target.SessionId is null)
            {
                continue;
            }

            foreach ((int firingPid, string probe) in signals)
            {
                // Coherently capture the process that fired (a child under `dotnet run`, not the
                // launcher), so a triggered snapshot carries provenance too. Don't auto-load it.
                SnapshotEntry entry = Capture(firingPid, load: false, reason: probe).Entry;
                (captured ??= []).Add((entry, probe));
            }
        }
        
        return (IReadOnlyList<(SnapshotEntry, string)>?)captured ?? [];
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
    /// captures the allocation state at the same instant as the dump, bundling it into the snapshot.
    /// </summary>
    public CaptureResult Capture(int pid, bool load = true, string? reason = null)
    {
        ProcessSupervisor? target = _targets.FirstOrDefault(t => !t.RootExited && Owns(t, pid));
        bool correlated = target is { HasCorrelation: true };

        string? provenance = null;
        long gcAtEmit = -1;
        if (target is not null && (correlated || target.ProfileOutPath is not null))
        {
            if (correlated)
            {
                // A unified provenance.slab (allocations + correlation) at this instant.
                (provenance, gcAtEmit) = target.RequestCorrelationSnapshot(pid, CaptureTimeout);
            }
            else
            {
                provenance = target.FlushAllocations(pid, CaptureTimeout);
            }
        }

        SnapshotEntry entry = Collect(pid, DumpKind.Heap, load: load, provenance: provenance, correlated: correlated, reason: reason);

        ProvenanceState state = ProvenanceState.None;
        if (entry.HasCorrelation)
        {
            // Drift: a GC between the emit and the external dump moves objects and stales the
            // address join. We can detect it (via the GC count) but not prevent it.
            bool drifted = gcAtEmit >= 0 && target!.GcCount(pid, DriftTimeout) is long now && now >= 0 && now != gcAtEmit;
            state = drifted ? ProvenanceState.Drifted : ProvenanceState.Exact;
        }
        return new CaptureResult(entry, state);
    }

    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DriftTimeout = TimeSpan.FromSeconds(3);

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
            target.Dispose(); // leaves the processes running; just releases handles
        }
    }
}

/// <summary>Whether a snapshot carries allocation provenance, and if the address join is trustworthy.</summary>
public enum ProvenanceState
{
    None,
    Exact,
    Drifted,
}

/// <summary>The outcome of <see cref="Workspace.Capture"/>: the new snapshot and its provenance state.</summary>
public sealed record CaptureResult(SnapshotEntry Entry, ProvenanceState Provenance);
