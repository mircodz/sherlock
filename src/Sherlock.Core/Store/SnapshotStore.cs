using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sherlock.Core.Store;

/// <summary>
/// The workspace library, persisted under <c>~/.sherlock</c> (override with the
/// <c>SHERLOCK_HOME</c> environment variable). Organized by <see cref="Session"/>:
/// each session is a directory holding its snapshots, log, and allocation profile.
/// </summary>
public sealed class SnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _catalogPath;
    private Catalog _catalog;

    public SnapshotStore(string root)
    {
        Root = root;
        _catalogPath = Path.Combine(root, "catalog.json");
        Directory.CreateDirectory(root);
        _catalog = Load(_catalogPath);
    }

    public string Root { get; }

    /// <summary>The default store, honoring <c>SHERLOCK_HOME</c>.</summary>
    public static SnapshotStore Default()
    {
        string? overridden = Environment.GetEnvironmentVariable("SHERLOCK_HOME");
        string root = !string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sherlock");
        return new SnapshotStore(root);
    }

    public IReadOnlyList<Session> Sessions => _catalog.Sessions.AsReadOnly();

    public Session? GetSession(string id) =>
        _catalog.Sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds a snapshot (and its owning session) by snapshot id or label.</summary>
    public (Session Session, SnapshotEntry Snapshot)? FindSnapshot(string idOrLabel)
    {
        foreach (Session session in _catalog.Sessions)
        {
            SnapshotEntry? snap = session.Snapshots.FirstOrDefault(s =>
                string.Equals(s.Id, idOrLabel, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Label, idOrLabel, StringComparison.OrdinalIgnoreCase));
            if (snap is not null)
            {
                return (session, snap);
            }
        }
        return null;
    }

    /// <summary>Creates an empty workspace and its directory.</summary>
    public Session BeginSession(
        SessionKind kind,
        string? command = null,
        bool withLog = false)
    {
        string id = $"w{_catalog.NextSession++}"; // w = workspace (a run)
        string dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);

        var session = new Session
        {
            Id = id,
            Kind = kind,
            Dir = dir,
            CreatedAt = DateTimeOffset.Now,
            Command = command,
            LogPath = withLog ? Path.Combine(dir, "run.log") : null,
        };

        _catalog.Sessions.Add(session);
        WriteMetadata(session);
        Save();
        return session;
    }

    /// <summary>Adds a dump to a session: moves it under the session's <c>snapshots/</c> when owned.</summary>
    public SnapshotEntry AddSnapshot(
        Session session,
        string sourcePath,
        bool moveIntoStore,
        string? label = null,
        int? sourcePid = null,
        string? sourceName = null,
        string? provenanceSource = null,
        bool correlated = false,
        string? reason = null)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Dump file not found.", sourcePath);
        }

        string id = $"s{_catalog.NextSnapshot++}";
        string finalPath;
        bool owned;
        if (moveIntoStore)
        {
            // A self-contained bundle folder: heap.dmp plus its provenance.slab.
            string bundleDir = Path.Combine(session.Dir, "snapshots", id);
            Directory.CreateDirectory(bundleDir);
            finalPath = Path.Combine(bundleDir, "heap.dmp");
            File.Move(sourcePath, finalPath, overwrite: true);

            if (provenanceSource is not null && File.Exists(provenanceSource))
            {
                File.Copy(provenanceSource, Path.Combine(bundleDir, "provenance.slab"), overwrite: true);
                try { File.Delete(provenanceSource); } catch { /* transient staging file */ }
            }

            owned = true;
        }
        else
        {
            finalPath = Path.GetFullPath(sourcePath);
            owned = false;
        }

        var entry = new SnapshotEntry(
            Id: id,
            Path: finalPath,
            Owned: owned,
            Label: label,
            CreatedAt: DateTimeOffset.Now,
            SizeBytes: new FileInfo(finalPath).Length)
        {
            Reason = reason,
            HasCorrelation = correlated,
        };

        // Attribute the snapshot to the process it came from; the first process seen in a
        // workspace is its root (a launched run sets its root explicitly, ahead of any snapshot).
        ProcessRecord process = session.GetOrAddProcess(
            sourcePid ?? 0, sourceName, isRoot: session.Processes.Count == 0);
        process.Snapshots.Add(entry);
        WriteMetadata(session);
        Save();
        return entry;
    }

    /// <summary>Creates a single-snapshot session (a one-off collect/import/crash).</summary>
    public (Session Session, SnapshotEntry Snapshot) RegisterStandalone(
        SessionKind kind,
        string sourcePath,
        bool moveIntoStore,
        string? sourceProcess = null,
        int? sourcePid = null,
        string? label = null)
    {
        Session session = BeginSession(kind, sourceProcess);
        SnapshotEntry snap = AddSnapshot(session, sourcePath, moveIntoStore, label, sourcePid, sourceProcess);
        return (session, snap);
    }

    /// <summary>Re-persists a session after external mutation (e.g. setting its pid).</summary>
    public void Persist(Session session)
    {
        WriteMetadata(session);
        Save();
    }

    /// <summary>Records that a process's exit-time allocation profile is present on disk.</summary>
    public void MarkAllocations(Session session, int pid, string? name, string allocationsPath)
    {
        session.GetOrAddProcess(pid, name).AllocationsPath = allocationsPath;
        WriteMetadata(session);
        Save();
    }

    /// <summary>Removes a whole session (id <c>rN</c>) or a single snapshot (id <c>sN</c>).</summary>
    public bool Remove(string id)
    {
        Session? session = GetSession(id);
        if (session is not null)
        {
            _catalog.Sessions.Remove(session);
            TryDeleteDir(session.Dir);
            Save();
            return true;
        }

        if (FindSnapshot(id) is not ({ } owner, { } snap))
        {
            return false;
        }

        owner.Processes.FirstOrDefault(p => p.Snapshots.Contains(snap))?.Snapshots.Remove(snap);
        if (snap.Owned)
        {
            TryDeleteDir(snap.Dir); // the bundle folder (heap.dmp + allocations + correlation)
        }
        
        WriteMetadata(owner);
        Save();
        
        return true;
    }

    public SnapshotEntry? SetLabel(string snapshotId, string? label)
    {
        if (FindSnapshot(snapshotId) is not ({ } session, { } snap))
        {
            return null;
        }

        SnapshotEntry updated = snap with { Label = label };
        ProcessRecord? process = session.Processes.FirstOrDefault(p => p.Snapshots.Contains(snap));
        if (process is not null)
        {
            process.Snapshots[process.Snapshots.IndexOf(snap)] = updated;
        }
        WriteMetadata(session);
        Save();
        return updated;
    }

    private void Save() => File.WriteAllText(_catalogPath, JsonSerializer.Serialize(_catalog, JsonOptions));

    /// <summary>Writes the session's self-describing record next to its artifacts.</summary>
    private void WriteMetadata(Session session)
    {
        try
        {
            Directory.CreateDirectory(session.Dir);
            File.WriteAllText(Path.Combine(session.Dir, "metadata.json"), JsonSerializer.Serialize(session, JsonOptions));
        }
        catch { /* best effort — the catalog remains the source of truth */ }
    }

    private static void TryDeleteDir(string dir)
    {
        try 
        { 
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch { /* best effort */ }
    }

    private static Catalog Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path), JsonOptions) ?? new Catalog();
            }
        }
        catch
        {
            // A corrupt catalog shouldn't brick the tool; start fresh.
        }
        
        return new Catalog();
    }

    private sealed class Catalog
    {
        public int SchemaVersion { get; set; } = 2;
        public int NextSession { get; set; } = 1;
        public int NextSnapshot { get; set; } = 1;
        public List<Session> Sessions { get; set; } = [];
    }
}
