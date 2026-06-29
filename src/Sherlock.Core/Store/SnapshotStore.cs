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
        Converters = { new JsonStringEnumConverter() },
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

    /// <summary>Creates an empty session and its directory.</summary>
    public Session BeginSession(
        SessionKind kind,
        string? sourceProcess = null,
        int? sourcePid = null,
        bool withLog = false,
        bool withAllocations = false)
    {
        string id = $"r{_catalog.NextSession++}";
        string dir = Path.Combine(Root, id);
        Directory.CreateDirectory(dir);

        var session = new Session
        {
            Id = id,
            Kind = kind,
            Dir = dir,
            CreatedAt = DateTimeOffset.Now,
            SourceProcess = sourceProcess,
            SourcePid = sourcePid,
            LogPath = withLog ? Path.Combine(dir, "run.log") : null,
            AllocationsPath = withAllocations ? Path.Combine(dir, "allocations.tsv") : null,
        };

        _catalog.Sessions.Add(session);
        WriteMetadata(session);
        Save();
        return session;
    }

    /// <summary>Adds a dump to a session: moves it under the session's <c>snapshots/</c> when owned.</summary>
    public SnapshotEntry AddSnapshot(Session session, string sourcePath, bool moveIntoStore, string? label = null)
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
            string snapsDir = Path.Combine(session.Dir, "snapshots");
            Directory.CreateDirectory(snapsDir);
            finalPath = Path.Combine(snapsDir, $"{id}.dmp");
            File.Move(sourcePath, finalPath, overwrite: true);
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
            SizeBytes: new FileInfo(finalPath).Length);

        session.Snapshots.Add(entry);
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
        Session session = BeginSession(kind, sourceProcess, sourcePid);
        SnapshotEntry snap = AddSnapshot(session, sourcePath, moveIntoStore, label);
        return (session, snap);
    }

    /// <summary>Re-persists a session after external mutation (e.g. setting its pid).</summary>
    public void Persist(Session session)
    {
        WriteMetadata(session);
        Save();
    }

    /// <summary>Records that a session's allocation profile is present on disk.</summary>
    public void MarkAllocations(Session session, string allocationsPath)
    {
        session.AllocationsPath = allocationsPath;
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

        owner.Snapshots.Remove(snap);
        if (snap.Owned)
        {
            try { if (File.Exists(snap.Path))
                {
                    File.Delete(snap.Path);
                }
            }
            catch { /* best effort */ }
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
        int i = session.Snapshots.IndexOf(snap);
        session.Snapshots[i] = updated;
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
        try { if (Directory.Exists(dir))
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
