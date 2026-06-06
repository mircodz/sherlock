using System;
using System.Collections.Generic;
using System.IO;
using Sherlock.Core;
using Sherlock.Core.Collection;
using Sherlock.Core.Store;

namespace Sherlock.CLI;

/// <summary>
/// The interactive workspace: the snapshot library plus the one snapshot that
/// analysis commands currently operate on. Loading a snapshot swaps the live
/// <see cref="DumpSession"/>; the previous one is disposed.
/// </summary>
public sealed class Workspace : IDisposable
{
    public Workspace(SnapshotStore store) => Store = store;

    public SnapshotStore Store { get; }

    private readonly List<ProcessSupervisor> _targets = new();

    /// <summary>Processes launched with <c>run</c> during this session.</summary>
    public IReadOnlyList<ProcessSupervisor> Targets => _targets;

    public void AddTarget(ProcessSupervisor supervisor) => _targets.Add(supervisor);

    /// <summary>The currently-loaded session, or null if nothing is loaded.</summary>
    public DumpSession? Current { get; private set; }

    /// <summary>The library entry backing <see cref="Current"/>, if it came from the library.</summary>
    public SnapshotEntry? CurrentEntry { get; private set; }

    /// <summary>Short label for the prompt (snapshot id, or a file name for transient loads).</summary>
    public string? CurrentName { get; private set; }

    /// <summary>Loads a catalogued snapshot as the current target.</summary>
    public void Load(SnapshotEntry entry)
    {
        DumpSession session = DumpSession.Open(entry.Path);
        Swap(session, entry, entry.Id);
    }

    /// <summary>Loads a dump file directly, without adding it to the library.</summary>
    public void LoadTransient(string path)
    {
        DumpSession session = DumpSession.Open(path);
        Swap(session, entry: null, Path.GetFileName(path));
    }

    /// <summary>
    /// Imports any crash dumps left behind by run-targets that have exited since the
    /// last check. Does not load them. Returns the newly catalogued entries.
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

            (imported ??= new List<SnapshotEntry>()).Add(Store.Register(
                sourcePath: path,
                moveIntoStore: true,
                origin: SnapshotOrigin.Crash,
                sourceProcess: target.RootName,
                sourcePid: target.RootPid));
        }
        return (IReadOnlyList<SnapshotEntry>?)imported ?? Array.Empty<SnapshotEntry>();
    }

    /// <summary>Collects a dump from a live process, catalogs it, and (by default) loads it.</summary>
    public SnapshotEntry Collect(int pid, DumpKind kind, SnapshotOrigin origin, bool load = true)
    {
        string temp = DumpCollector.Collect(pid, kind, outputPath: null);
        SnapshotEntry entry = Store.Register(
            sourcePath: temp,
            moveIntoStore: true,
            origin: origin,
            sourceProcess: NameOf(pid),
            sourcePid: pid);
        if (load)
        {
            Load(entry);
        }

        return entry;
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
        CurrentName = null;
    }

    private void Swap(DumpSession session, SnapshotEntry? entry, string name)
    {
        Current?.Dispose();
        Current = session;
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
