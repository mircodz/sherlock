using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sherlock.Core.Store;

/// <summary>
/// The workspace snapshot library, persisted under <c>~/.sherlock</c> (override
/// with the <c>SHERLOCK_HOME</c> environment variable). Owns a JSON catalog plus
/// a <c>snapshots/</c> directory for dumps Sherlock itself produces.
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
        SnapshotsDir = Path.Combine(root, "snapshots");
        _catalogPath = Path.Combine(root, "catalog.json");

        Directory.CreateDirectory(SnapshotsDir);
        _catalog = Load(_catalogPath);
    }

    public string Root { get; }
    public string SnapshotsDir { get; }

    /// <summary>The default store, honoring <c>SHERLOCK_HOME</c>.</summary>
    public static SnapshotStore Default()
    {
        string? overridden = Environment.GetEnvironmentVariable("SHERLOCK_HOME");
        string root = !string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sherlock");
        return new SnapshotStore(root);
    }

    public IReadOnlyList<SnapshotEntry> List() => _catalog.Snapshots.AsReadOnly();

    /// <summary>Resolves a snapshot by id (e.g. <c>s3</c>) or by exact label.</summary>
    public SnapshotEntry? Get(string idOrLabel) =>
        _catalog.Snapshots.FirstOrDefault(s =>
            string.Equals(s.Id, idOrLabel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Label, idOrLabel, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds a dump to the library. When <paramref name="moveIntoStore"/> is true the
    /// file is moved under <c>snapshots/</c> and owned; otherwise it is referenced in place.
    /// </summary>
    public SnapshotEntry Register(
        string sourcePath,
        bool moveIntoStore,
        SnapshotOrigin origin,
        string? sourceProcess = null,
        int? sourcePid = null,
        string? label = null)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Dump file not found.", sourcePath);
        }

        string id = $"s{_catalog.NextId++}";

        string finalPath;
        bool owned;
        if (moveIntoStore)
        {
            finalPath = Path.Combine(SnapshotsDir, $"{id}.dmp");
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
            SourceProcess: sourceProcess,
            SourcePid: sourcePid,
            Origin: origin,
            SizeBytes: new FileInfo(finalPath).Length);

        _catalog.Snapshots.Add(entry);
        Save();
        return entry;
    }

    public bool Remove(string id)
    {
        SnapshotEntry? entry = Get(id);
        if (entry is null)
        {
            return false;
        }

        _catalog.Snapshots.Remove(entry);
        if (entry.Owned)
        {
            try { if (File.Exists(entry.Path))
                {
                    File.Delete(entry.Path);
                }
            }
            catch { /* best effort */ }
        }
        Save();
        return true;
    }

    public SnapshotEntry? SetLabel(string id, string? label)
    {
        int index = _catalog.Snapshots.FindIndex(s =>
            string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        SnapshotEntry updated = _catalog.Snapshots[index] with { Label = label };
        _catalog.Snapshots[index] = updated;
        Save();
        return updated;
    }

    private void Save()
    {
        string json = JsonSerializer.Serialize(_catalog, JsonOptions);
        File.WriteAllText(_catalogPath, json);
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
        public int NextId { get; set; } = 1;
        public List<SnapshotEntry> Snapshots { get; set; } = [];
    }
}
