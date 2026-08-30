using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Sherlock.Core.Profiling;

namespace Sherlock.Core.Store;

/// <summary>Persistent snapshot library under <c>~/.sherlock</c>.</summary>
public sealed class SnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Lock _lock = new();
    private readonly List<Session> _sessions;
    private int _nextSession;
    private int _nextSnapshot;

    public SnapshotStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        _sessions = ReadSessions(Root);
        _nextSession = NextId(Directory.EnumerateDirectories(Root).Select(Path.GetFileName), 'w');
        _nextSnapshot = NextSnapshotIdOnDisk();
    }

    public string Root { get; }

    public static SnapshotStore Default()
    {
        string? configured = Environment.GetEnvironmentVariable("SHERLOCK_HOME");
        return new SnapshotStore(string.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sherlock") : configured);
    }

    public IReadOnlyList<Session> Sessions
    {
        get
        {
            lock (_lock)
            {
                return _sessions.ToArray();
            }
        }
    }

    public Session? GetSession(string id)
    {
        lock (_lock)
        {
            return FindSession(id);
        }
    }

    public (Session Session, SnapshotEntry Snapshot)? FindSnapshot(string idOrLabel)
    {
        lock (_lock)
        {
            return FindSnapshotEntry(idOrLabel);
        }
    }

    public Session BeginSession(SessionKind kind, string? command = null, bool withLog = false)
    {
        lock (_lock)
        {
            string id = NextSessionId();
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

            try
            {
                WriteSession(session);
                _sessions.Add(session);
                return session;
            }
            catch
            {
                DeleteDir(dir);
                throw;
            }
        }
    }

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
        lock (_lock)
        {
            CheckSnapshot(sourcePath, moveIntoStore, provenanceSource, correlated);
            string id = NextSnapshotId();
            string path = Path.GetFullPath(sourcePath);
            string? dir = null;
            SnapshotEntry? snapshot = null;
            ProcessRecord? process = null;
            bool addedProcess = false;

            try
            {
                if (moveIntoStore)
                {
                    dir = Path.Combine(session.Dir, "snapshots", id);
                    Directory.CreateDirectory(dir);
                    path = Path.Combine(dir, "heap.dmp");
                    Move(sourcePath, path);
                    if (provenanceSource is not null)
                    {
                        File.Copy(provenanceSource, Path.Combine(dir, "provenance.slab"));
                    }
                }

                snapshot = new SnapshotEntry(id, path, moveIntoStore, label, DateTimeOffset.Now, new FileInfo(path).Length)
                {
                    Reason = reason,
                    HasCorrelation = correlated,
                    ProvenanceSizeBytes = provenanceSource is null ? 0 : new FileInfo(Path.Combine(dir!, "provenance.slab")).Length,
                };

                int pid = sourcePid ?? 0;
                process = session.Processes.FirstOrDefault(p => p.Pid == pid);
                if (process is null)
                {
                    process = session.GetOrAddProcess(pid, sourceName, isRoot: session.Processes.Count == 0);
                    addedProcess = true;
                }
                else
                {
                    process.Name ??= sourceName;
                }

                process.Snapshots.Add(snapshot);
                WriteSession(session);
                DeleteFile(provenanceSource);
                return snapshot;
            }
            catch (Exception failure)
            {
                if (snapshot is not null && process is not null)
                {
                    process.Snapshots.Remove(snapshot);
                }
                if (addedProcess && process is not null)
                {
                    session.Processes.Remove(process);
                }
                if (dir is not null && Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    throw new IOException($"Could not save snapshot; files preserved at '{dir}'.", failure);
                }
                DeleteDir(dir);
                throw;
            }
        }
    }

    public (Session Session, SnapshotEntry Snapshot) RegisterStandalone(
        SessionKind kind,
        string sourcePath,
        bool moveIntoStore,
        string? sourceProcess = null,
        int? sourcePid = null,
        string? label = null)
    {
        Session session = BeginSession(kind, sourceProcess);
        SnapshotEntry snapshot = AddSnapshot(session, sourcePath, moveIntoStore, label, sourcePid, sourceProcess);
        return (session, snapshot);
    }

    public void Persist(Session session)
    {
        lock (_lock)
        {
            WriteSession(session);
        }
    }

    public void MarkAllocations(Session session, int pid, string? name, string allocationsPath)
    {
        ProvenanceReader.ValidateFile(allocationsPath);
        lock (_lock)
        {
            var process = session.GetOrAddProcess(pid, name);
            string? previous = process.AllocationsPath;
            process.AllocationsPath = allocationsPath;
            try
            {
                WriteSession(session);
            }
            catch
            {
                process.AllocationsPath = previous;
                throw;
            }
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            if (FindSession(id) is { } session)
            {
                Directory.Delete(session.Dir, recursive: true);
                _sessions.Remove(session);
                return true;
            }
            if (FindSnapshotEntry(id) is not ({ } owner, { } snapshot))
            {
                return false;
            }

            var process = owner.Processes.FirstOrDefault(p => p.Snapshots.Contains(snapshot));
            if (process is null)
            {
                return false;
            }
            int index = process.Snapshots.IndexOf(snapshot);
            process.Snapshots.RemoveAt(index);
            try
            {
                WriteSession(owner);
            }
            catch
            {
                process.Snapshots.Insert(index, snapshot);
                throw;
            }
            if (snapshot.Owned)
            {
                DeleteDir(snapshot.Dir);
            }
            return true;
        }
    }

    public SnapshotEntry? SetLabel(string id, string? label)
    {
        lock (_lock)
        {
            if (FindSnapshotEntry(id) is not ({ } session, { } snapshot))
            {
                return null;
            }
            var process = session.Processes.FirstOrDefault(p => p.Snapshots.Contains(snapshot));
            if (process is null)
            {
                return null;
            }

            int index = process.Snapshots.IndexOf(snapshot);
            var updated = snapshot with { Label = label };
            process.Snapshots[index] = updated;
            try
            {
                WriteSession(session);
                return updated;
            }
            catch
            {
                process.Snapshots[index] = snapshot;
                throw;
            }
        }
    }

    private Session? FindSession(string id) => _sessions.FirstOrDefault(session => string.Equals(session.Id, id, StringComparison.OrdinalIgnoreCase));

    private (Session, SnapshotEntry)? FindSnapshotEntry(string idOrLabel)
    {
        foreach (Session session in _sessions)
        {
            var snapshot = session.Snapshots.FirstOrDefault(snapshot => string.Equals(snapshot.Id, idOrLabel, StringComparison.OrdinalIgnoreCase) || string.Equals(snapshot.Label, idOrLabel, StringComparison.OrdinalIgnoreCase));
            if (snapshot is not null)
            {
                return (session, snapshot);
            }
        }
        return null;
    }

    private string NextSessionId()
    {
        while (Directory.Exists(Path.Combine(Root, $"w{_nextSession}")))
        {
            _nextSession++;
        }
        return $"w{_nextSession++}";
    }

    private string NextSnapshotId()
    {
        while (SnapshotExists($"s{_nextSnapshot}"))
        {
            _nextSnapshot++;
        }
        return $"s{_nextSnapshot++}";
    }

    private bool SnapshotExists(string id)
    {
        if (FindSnapshotEntry(id) is not null)
        {
            return true;
        }
        return Directory.EnumerateDirectories(Root, "w*").Any(dir => Directory.Exists(Path.Combine(dir, "snapshots", id)));
    }

    private int NextSnapshotIdOnDisk()
    {
        var ids = _sessions.SelectMany(session => session.Snapshots).Select(snapshot => snapshot.Id).ToList();
        foreach (string workspace in Directory.EnumerateDirectories(Root, "w*"))
        {
            string snapshots = Path.Combine(workspace, "snapshots");
            if (Directory.Exists(snapshots))
            {
                ids.AddRange(Directory.EnumerateDirectories(snapshots).Select(Path.GetFileName)!);
            }
        }
        return NextId(ids, 's');
    }

    private static int NextId(IEnumerable<string?> ids, char prefix)
    {
        int max = 0;
        foreach (string? id in ids)
        {
            max = Math.Max(max, IdNumber(id, prefix));
        }
        return max + 1;
    }

    private static List<Session> ReadSessions(string root)
    {
        var sessions = new List<Session>();
        foreach (string dir in Directory.EnumerateDirectories(root, "w*").OrderBy(path => IdNumber(Path.GetFileName(path), 'w')))
        {
            string path = Path.Combine(dir, "metadata.json");
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                Session session = JsonSerializer.Deserialize<Session>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException($"Workspace metadata is empty: {path}");
                session.Dir = dir;
                NormalizePaths(session);
                sessions.Add(session);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Workspace metadata is invalid: {path}: {ex.Message}", ex);
            }
        }
        return sessions;
    }

    private static int IdNumber(string? id, char prefix) => id?.Length > 1 && id[0] == prefix && int.TryParse(id[1..], out int value) ? value : 0;

    private static void NormalizePaths(Session session)
    {
        foreach (ProcessRecord process in session.Processes)
        {
            for (int i = 0; i < process.Snapshots.Count; i++)
            {
                SnapshotEntry snapshot = process.Snapshots[i];
                if (snapshot.Owned)
                {
                    process.Snapshots[i] = snapshot with { Path = Path.Combine(session.Dir, "snapshots", snapshot.Id, "heap.dmp") };
                }
            }
        }
    }

    private static void CheckSnapshot(string dump, bool owned, string? provenance, bool correlated)
    {
        if (!File.Exists(dump))
        {
            throw new FileNotFoundException("Dump file not found.", dump);
        }
        if (correlated && provenance is null)
        {
            throw new InvalidDataException("A correlated snapshot requires allocation provenance.");
        }
        if (!owned && provenance is not null)
        {
            throw new InvalidOperationException("Allocation provenance requires an owned snapshot.");
        }
        if (provenance is not null)
        {
            ProvenanceReader.ValidateFile(provenance);
        }
    }

    private static void WriteSession(Session session) => WriteJson(Path.Combine(session.Dir, "metadata.json"), session);

    private static void WriteJson<T>(string path, T value)
    {
        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            DeleteFile(tmp);
        }
    }

    private static void Move(string source, string destination)
    {
        try
        {
            File.Move(source, destination);
        }
        catch (IOException) when (!File.Exists(destination))
        {
            File.Copy(source, destination);
            File.Delete(source);
        }
    }

    private static void DeleteFile(string? path)
    {
        if (path is null)
        {
            return;
        }
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteDir(string? path)
    {
        if (path is null)
        {
            return;
        }
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
