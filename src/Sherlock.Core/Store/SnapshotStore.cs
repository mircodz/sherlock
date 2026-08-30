using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sherlock.Core.Profiling;
using Sherlock.Core.Storage;

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
    private readonly object _gate = new();
    private Catalog _catalog;

    public SnapshotStore(string root)
    {
        Root = root;
        _catalogPath = Path.Combine(root, "catalog.json");
        Directory.CreateDirectory(root);
        SecureDirectory(root);
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

    public IReadOnlyList<Session> Sessions
    {
        get
        {
            lock (_gate)
            {
                return _catalog.Sessions.ToArray();
            }
        }
    }

    public Session? GetSession(string id)
    {
        lock (_gate)
        {
            return _catalog.Sessions.FirstOrDefault(
                s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Finds a snapshot (and its owning session) by snapshot id or label.</summary>
    public (Session Session, SnapshotEntry Snapshot)? FindSnapshot(string idOrLabel)
    {
        lock (_gate)
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
    }

    /// <summary>Creates an empty workspace and its directory.</summary>
    public Session BeginSession(
        SessionKind kind,
        string? command = null,
        bool withLog = false)
    {
        lock (_gate)
        {
            string id = $"w{_catalog.NextSession++}"; // w = workspace (a run)
            string dir = Path.Combine(Root, id);
            Directory.CreateDirectory(dir);
            SecureDirectory(dir);

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
            try
            {
                WriteMetadata(session);
                Save();
                return session;
            }
            catch
            {
                _catalog.Sessions.Remove(session);
                _catalog.NextSession--;
                TryDeleteDir(dir);
                throw;
            }
        }
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
        lock (_gate)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Dump file not found.", sourcePath);
            }
            if (correlated && provenanceSource is null)
            {
                throw new InvalidDataException("A correlated snapshot requires allocation provenance.");
            }
            if (!moveIntoStore && provenanceSource is not null)
            {
                throw new InvalidOperationException(
                    "Allocation provenance can only be attached to an owned snapshot bundle.");
            }
            if (provenanceSource is not null)
            {
                ValidateProvenance(provenanceSource);
            }

            string id = $"s{_catalog.NextSnapshot++}";
            string? bundleDir = null;
            string? stagingDir = null;
            string finalPath = Path.GetFullPath(sourcePath);
            bool owned = false;
            ProcessRecord? process = null;
            SnapshotEntry? entry = null;
            bool processAdded = false;

            try
            {
                if (moveIntoStore)
                {
                    string snapshotsDir = Path.Combine(session.Dir, "snapshots");
                    Directory.CreateDirectory(snapshotsDir);
                    SecureDirectory(snapshotsDir);
                    bundleDir = Path.Combine(snapshotsDir, id);
                    stagingDir = $"{bundleDir}.tmp-{Guid.NewGuid():N}";
                    Directory.CreateDirectory(stagingDir);
                    SecureDirectory(stagingDir);

                    string stagedHeap = Path.Combine(stagingDir, "heap.dmp");
                    MoveFile(sourcePath, stagedHeap);
                    SecureFile(stagedHeap);
                    if (provenanceSource is not null)
                    {
                        string stagedProvenance = Path.Combine(stagingDir, "provenance.slab");
                        File.Copy(provenanceSource, stagedProvenance, overwrite: false);
                        SecureFile(stagedProvenance);
                    }

                    Directory.Move(stagingDir, bundleDir);
                    stagingDir = null;
                    finalPath = Path.Combine(bundleDir, "heap.dmp");
                    owned = true;
                }

                entry = new SnapshotEntry(
                    Id: id,
                    Path: finalPath,
                    Owned: owned,
                    Label: label,
                    CreatedAt: DateTimeOffset.Now,
                    SizeBytes: new FileInfo(finalPath).Length)
                {
                    Reason = reason,
                    HasCorrelation = correlated,
                    ProvenanceSizeBytes = provenanceSource is null
                        ? 0
                        : new FileInfo(Path.Combine(bundleDir!, "provenance.slab")).Length,
                };

                int pid = sourcePid ?? 0;
                process = session.Processes.FirstOrDefault(p => p.Pid == pid);
                if (process is null)
                {
                    process = session.GetOrAddProcess(
                        pid, sourceName, isRoot: session.Processes.Count == 0);
                    processAdded = true;
                }
                else
                {
                    process.Name ??= sourceName;
                }

                process.Snapshots.Add(entry);
                WriteMetadata(session);
                Save();

                if (moveIntoStore && provenanceSource is not null)
                {
                    TryDeleteFile(provenanceSource);
                }
                return entry;
            }
            catch (Exception failure)
            {
                if (entry is not null && process is not null)
                {
                    process.Snapshots.Remove(entry);
                }
                if (processAdded && process is not null)
                {
                    session.Processes.Remove(process);
                }
                _catalog.NextSnapshot--;

                string? stagedHeap = stagingDir is null
                    ? bundleDir is null ? null : Path.Combine(bundleDir, "heap.dmp")
                    : Path.Combine(stagingDir, "heap.dmp");
                bool heapRestored = stagedHeap is null || !File.Exists(stagedHeap) || File.Exists(sourcePath);
                if (!heapRestored)
                {
                    heapRestored = TryMoveFile(stagedHeap!, sourcePath);
                }
                if (heapRestored && stagingDir is not null)
                {
                    TryDeleteDir(stagingDir);
                }
                if (heapRestored && bundleDir is not null)
                {
                    TryDeleteDir(bundleDir);
                }
                TryWriteMetadata(session);
                if (!heapRestored)
                {
                    throw new IOException(
                        $"Snapshot metadata could not be saved, and the heap dump was preserved at '{stagedHeap}'.",
                        failure);
                }
                throw;
            }
        }
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
        lock (_gate)
        {
            WriteMetadata(session);
            Save();
        }
    }

    /// <summary>Records that a process's exit-time allocation profile is present on disk.</summary>
    public void MarkAllocations(Session session, int pid, string? name, string allocationsPath)
    {
        ValidateProvenance(allocationsPath);
        SecureFile(allocationsPath);
        lock (_gate)
        {
            session.GetOrAddProcess(pid, name).AllocationsPath = allocationsPath;
            WriteMetadata(session);
            Save();
        }
    }

    /// <summary>Removes a whole session (id <c>rN</c>) or a single snapshot (id <c>sN</c>).</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            Session? session = GetSession(id);
            if (session is not null)
            {
                int index = _catalog.Sessions.IndexOf(session);
                _catalog.Sessions.RemoveAt(index);
                try
                {
                    Save();
                }
                catch
                {
                    _catalog.Sessions.Insert(index, session);
                    throw;
                }
                TryDeleteDir(session.Dir);
                return true;
            }

            if (FindSnapshot(id) is not ({ } owner, { } snap))
            {
                return false;
            }

            ProcessRecord? process = owner.Processes.FirstOrDefault(p => p.Snapshots.Contains(snap));
            if (process is null)
            {
                return false;
            }
            int snapshotIndex = process.Snapshots.IndexOf(snap);
            process.Snapshots.RemoveAt(snapshotIndex);
            try
            {
                WriteMetadata(owner);
                Save();
            }
            catch
            {
                process.Snapshots.Insert(snapshotIndex, snap);
                TryWriteMetadata(owner);
                throw;
            }

            if (snap.Owned)
            {
                TryDeleteDir(snap.Dir); // the bundle folder (heap.dmp + allocations + correlation)
            }

            return true;
        }
    }

    public SnapshotEntry? SetLabel(string snapshotId, string? label)
    {
        lock (_gate)
        {
            if (FindSnapshot(snapshotId) is not ({ } session, { } snap))
            {
                return null;
            }

            SnapshotEntry updated = snap with { Label = label };
            ProcessRecord? process = session.Processes.FirstOrDefault(p => p.Snapshots.Contains(snap));
            if (process is not null)
            {
                int index = process.Snapshots.IndexOf(snap);
                process.Snapshots[index] = updated;
                try
                {
                    WriteMetadata(session);
                    Save();
                }
                catch
                {
                    process.Snapshots[index] = snap;
                    TryWriteMetadata(session);
                    throw;
                }
                return updated;
            }
            return null;
        }
    }

    private void Save() =>
        WriteTextAtomic(_catalogPath, JsonSerializer.Serialize(_catalog, JsonOptions));

    /// <summary>Writes the session's self-describing record next to its artifacts.</summary>
    private void WriteMetadata(Session session)
    {
        Directory.CreateDirectory(session.Dir);
        WriteTextAtomic(
            Path.Combine(session.Dir, "metadata.json"),
            JsonSerializer.Serialize(session, JsonOptions));
    }

    private void TryWriteMetadata(Session session)
    {
        try
        {
            WriteMetadata(session);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateProvenance(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Allocation provenance file not found.", path);
        }
        ProvenanceReader.ValidateFile(path);
    }

    private static void WriteTextAtomic(string path, string contents)
    {
        string temp = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temp, contents);
            SecureFile(temp);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private static void MoveFile(string source, string destination)
    {
        try
        {
            File.Move(source, destination);
        }
        catch (IOException) when (!File.Exists(destination))
        {
            File.Copy(source, destination, overwrite: false);
            File.Delete(source);
        }
    }

    private static bool TryMoveFile(string source, string destination)
    {
        try
        {
            MoveFile(source, destination);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void SecureDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
    }

    private static void SecureFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite);
        }
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
