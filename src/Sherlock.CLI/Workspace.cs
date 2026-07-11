using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sherlock.Core;
using Sherlock.Core.Collection;
using Sherlock.Core.Store;

namespace Sherlock.CLI;

/// <summary>
/// The interactive workspace: the session library plus the one snapshot that
/// analysis commands currently operate on. Loading a snapshot swaps the live
/// <see cref="DumpSession"/>; the previous one is disposed.
/// </summary>
public sealed class Workspace(SnapshotStore store) : IDisposable
{
    public SnapshotStore Store { get; } = store;

    private readonly List<ProcessSupervisor> _targets = [];

    /// <summary>Processes launched with <c>run</c> during this session.</summary>
    public IReadOnlyList<ProcessSupervisor> Targets => _targets;

    public void AddTarget(ProcessSupervisor supervisor) => _targets.Add(supervisor);

    /// <summary>The currently-loaded dump session, or null if nothing is loaded.</summary>
    public DumpSession? Current { get; private set; }

    /// <summary>The snapshot backing <see cref="Current"/>, if it came from the library.</summary>
    public SnapshotEntry? CurrentEntry { get; private set; }

    /// <summary>The session that owns <see cref="CurrentEntry"/>.</summary>
    public Session? CurrentSession { get; private set; }

    /// <summary>Short label for the prompt (snapshot id, or a file name for transient loads).</summary>
    public string? CurrentName { get; private set; }

    /// <summary>Loads a catalogued snapshot as the current target.</summary>
    public void Load(Session session, SnapshotEntry entry)
    {
        DumpSession dump = DumpSession.Open(entry.Path);
        Swap(dump, session, entry, entry.Id);
    }

    /// <summary>Loads a dump file directly, without adding it to the library.</summary>
    public void LoadTransient(string path)
    {
        DumpSession dump = DumpSession.Open(path);
        Swap(dump, session: null, entry: null, Path.GetFileName(path));
    }

    /// <summary>
    /// Imports crash dumps left behind by run-targets that have exited since the last
    /// check, attaching each to its run's session. Returns the new snapshots.
    /// </summary>
    public IReadOnlyList<SnapshotEntry> HarvestExitedCrashDumps()
    {
        List<SnapshotEntry>? imported = null;
        foreach (ProcessSupervisor target in _targets)
        {
            string? path = target.TryHarvestRootCrashDump();
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

    /// <summary>
    /// Records allocation profiles from <c>run --profile</c> targets that have exited
    /// (and so flushed). The profile already lives in the session dir; this just marks it.
    /// </summary>
    public IReadOnlyList<Session> HarvestExitedAllocationProfiles()
    {
        List<Session>? marked = null;
        foreach (ProcessSupervisor target in _targets)
        {
            if (target.SessionId is null || Store.GetSession(target.SessionId) is not { } session)
            {
                continue;
            }

            // Every .NET process in the tree flushes its own per-pid profile — attribute each to
            // its process, so both the launcher and the app carry their allocation data.
            IReadOnlyList<(int Pid, string Path)> profiles = target.HarvestAllocationProfiles();
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

    /// <summary>
    /// For each run-target whose probes have signalled a snapshot request since the last
    /// check, captures a live heap dump into that run's session, labelled with the probe.
    /// Returns the new snapshots paired with the probe that triggered them.
    /// </summary>
    public IReadOnlyList<(SnapshotEntry Entry, string Probe)> HarvestProbeSnapshots()
    {
        List<(SnapshotEntry, string)>? captured = null;
        foreach (ProcessSupervisor target in _targets)
        {
            IReadOnlyList<(int Pid, string Name)> signals = target.TryHarvestProbeSignals();
            if (signals.Count == 0 || target.SessionId is null)
            {
                continue;
            }

            Session? session = Store.GetSession(target.SessionId);
            if (session is null)
            {
                continue;
            }

            foreach ((int firingPid, string probe) in signals)
            {
                // Dump the process that actually fired (a child under `dotnet run`, not the launcher).
                string temp = DumpCollector.Collect(firingPid, DumpKind.Heap, outputPath: null);
                SnapshotEntry entry = Store.AddSnapshot(session, temp, moveIntoStore: true,
                    sourcePid: firingPid, sourceName: target.NameFor(firingPid) ?? NameOf(firingPid), reason: probe);
                (captured ??= []).Add((entry, probe));
            }
        }
        
        return (IReadOnlyList<(SnapshotEntry, string)>?)captured ?? [];
    }

    /// <summary>Collects a dump from a live process (sl-side WriteDump), catalogs it, and loads it.</summary>
    public SnapshotEntry Collect(int pid, DumpKind kind, bool load = true, string? provenance = null, bool correlated = false)
    {
        string temp = DumpCollector.Collect(pid, kind, outputPath: null);
        return Ingest(pid, temp, load, provenance, correlated);
    }

    /// <summary>
    /// Catalogs an already-written dump file (e.g. one the profiler self-dumped) under the
    /// run session this pid belongs to, and optionally loads it.
    /// </summary>
    public SnapshotEntry Ingest(int pid, string dumpPath, bool load = true, string? provenance = null, bool correlated = false)
    {
        // A run is one workspace spanning its whole process tree — attribute the snapshot to the
        // run that owns this pid (root OR a live descendant), so snapshots of children land in the
        // run's workspace rather than a stray collect session.
        ProcessSupervisor? target = _targets.FirstOrDefault(t => t.SessionId is not null && Owns(t, pid));
        Session session = target?.SessionId is { } sid && Store.GetSession(sid) is { } s
            ? s
            : Store.BeginSession(SessionKind.Collect, NameOf(pid));

        SnapshotEntry entry = Store.AddSnapshot(session, dumpPath, moveIntoStore: true,
            sourcePid: pid, sourceName: NameOf(pid) ?? target?.RootName,
            provenanceSource: provenance, correlated: correlated);
        if (load)
        {
            Load(session, entry);
        }
        
        return entry;
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

    private void Swap(DumpSession dump, Session? session, SnapshotEntry? entry, string name)
    {
        Current?.Dispose();
        Current = dump;
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
