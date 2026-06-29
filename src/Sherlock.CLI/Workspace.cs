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
public sealed class Workspace : IDisposable
{
    public Workspace(SnapshotStore store) => Store = store;

    public SnapshotStore Store { get; }

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
            (imported ??= []).Add(Store.AddSnapshot(session, path, moveIntoStore: true));
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
            string? path = target.TryHarvestAllocationProfile();
            if (path is null || target.SessionId is null)
            {
                continue;
            }

            Session? session = Store.GetSession(target.SessionId);
            if (session is null)
            {
                continue;
            }

            Store.MarkAllocations(session, path);
            (marked ??= []).Add(session);
        }
        return (IReadOnlyList<Session>?)marked ?? [];
    }

    /// <summary>Collects a dump from a live process, catalogs it under the right session, and loads it.</summary>
    public SnapshotEntry Collect(int pid, DumpKind kind, bool load = true)
    {
        string temp = DumpCollector.Collect(pid, kind, outputPath: null);

        // Attach to the run session this pid belongs to, else a fresh collect session.
        ProcessSupervisor? target = _targets.FirstOrDefault(t => t.RootPid == pid && t.SessionId is not null);
        Session session = target?.SessionId is { } sid && Store.GetSession(sid) is { } s
            ? s
            : Store.BeginSession(SessionKind.Collect, NameOf(pid), pid);

        SnapshotEntry entry = Store.AddSnapshot(session, temp, moveIntoStore: true);
        if (load)
        {
            Load(session, entry);
        }
        return entry;
    }

    /// <summary>Finds (or lazily creates) the library session a run-target belongs to.</summary>
    private Session SessionFor(ProcessSupervisor target, SessionKind fallbackKind)
    {
        if (target.SessionId is { } sid && Store.GetSession(sid) is { } existing)
        {
            return existing;
        }
        return Store.BeginSession(fallbackKind, target.RootName, target.RootPid);
    }

    private static string? NameOf(int pid)
    {
        try { return System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
        catch { return null; }
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
            target.Dispose(); // leaves the processes running; just releases handles
    }
}
