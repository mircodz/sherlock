using System.Text.Json;
using Sherlock.Core;

namespace Sherlock.Profiler;

/// <summary>
/// Manages multiple heap snapshots, allowing users to switch between them and compare.
/// </summary>
public class SnapshotManager : IDisposable
{
    private readonly Dictionary<int, SnapshotEntry> _snapshots = new();
    private readonly string _snapshotsDirectory;
    private int _nextSnapshotId = 1;
    private int? _currentSnapshotId;

    public SnapshotManager()
    {
        _snapshotsDirectory = Path.Combine(Path.GetTempPath(), "sherlock_snapshots");
        Directory.CreateDirectory(_snapshotsDirectory);
        LoadExistingSnapshots();
    }

    public int? CurrentSnapshotId => _currentSnapshotId;
    public IReadOnlyDictionary<int, SnapshotEntry> Snapshots => _snapshots;

    /// <summary>
    /// Takes a new snapshot and adds it to the collection.
    /// </summary>
    public async Task<int> TakeSnapshotAsync(int processId)
    {
        var snapshotId = _nextSnapshotId++;
        var timestamp = DateTime.Now;
        
        Console.WriteLine($"Taking snapshot #{snapshotId} of process {processId}...");
        
        try
        {
            var snapshot = await SnapshotCapture.TakeSnapshotAsync(processId);
            
            var dumpFileName = $"snapshot_{snapshotId}_{timestamp:yyyyMMdd_HHmmss}.dmp";
            var dumpPath = Path.Combine(_snapshotsDirectory, dumpFileName);
            
            var metadataFileName = $"snapshot_{snapshotId}_metadata.json";
            var metadataPath = Path.Combine(_snapshotsDirectory, metadataFileName);
            
            var entry = new SnapshotEntry
            {
                Id = snapshotId,
                ProcessId = processId,
                Timestamp = timestamp,
                DumpFilePath = dumpPath,
                MetadataFilePath = metadataPath,
                TotalObjects = snapshot.TotalObjects,
                TotalMemory = snapshot.TotalMemory
            };

            // Save the dump file (move from temp location)
            if (!string.IsNullOrEmpty(snapshot.TempDumpPath) && File.Exists(snapshot.TempDumpPath))
            {
                File.Move(snapshot.TempDumpPath, dumpPath);
            }

            // Save metadata
            await SaveSnapshotMetadata(entry, snapshot);
            
            _snapshots[snapshotId] = entry;
            _currentSnapshotId = snapshotId;
            
            Console.WriteLine($"✓ Snapshot #{snapshotId} saved: {entry.TotalObjects:N0} objects, {FormatBytes(entry.TotalMemory)}");
            Console.WriteLine($"  Dump: {dumpFileName}");
            
            snapshot.Dispose();
            return snapshotId;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to take snapshot #{snapshotId}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Switches to a specific snapshot by ID.
    /// </summary>
    public async Task<HeapSnapshot?> SwitchToSnapshotAsync(int snapshotId)
    {
        if (!_snapshots.TryGetValue(snapshotId, out var entry))
        {
            Console.WriteLine($"Snapshot #{snapshotId} not found");
            return null;
        }

        if (!File.Exists(entry.DumpFilePath))
        {
            Console.WriteLine($"Dump file for snapshot #{snapshotId} not found: {entry.DumpFilePath}");
            return null;
        }

        try
        {
            Console.WriteLine($"Loading snapshot #{snapshotId}...");
            var snapshot = await LoadSnapshotFromDump(entry.DumpFilePath);
            _currentSnapshotId = snapshotId;
            
            Console.WriteLine($"✓ Switched to snapshot #{snapshotId}");
            return snapshot;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load snapshot #{snapshotId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Lists all available snapshots.
    /// </summary>
    public void ListSnapshots()
    {
        if (!_snapshots.Any())
        {
            Console.WriteLine("No snapshots available");
            return;
        }

        Console.WriteLine("\nAvailable snapshots:");
        Console.WriteLine("ID  | Timestamp           | Process | Objects     | Memory     ");
        Console.WriteLine("----|---------------------|---------|-------------|------------");

        foreach (var entry in _snapshots.Values.OrderBy(s => s.Id))
        {
            var current = entry.Id == _currentSnapshotId ? "*" : " ";
            Console.WriteLine($"{current}{entry.Id,2} | {entry.Timestamp:yyyy-MM-dd HH:mm:ss} | {entry.ProcessId,7} | {entry.TotalObjects,11:N0} | {FormatBytes(entry.TotalMemory),10}");
        }
    }

    /// <summary>
    /// Deletes a snapshot and its associated files.
    /// </summary>
    public void DeleteSnapshot(int snapshotId)
    {
        if (!_snapshots.TryGetValue(snapshotId, out var entry))
        {
            Console.WriteLine($"Snapshot #{snapshotId} not found");
            return;
        }

        try
        {
            if (File.Exists(entry.DumpFilePath))
                File.Delete(entry.DumpFilePath);
            
            if (File.Exists(entry.MetadataFilePath))
                File.Delete(entry.MetadataFilePath);

            _snapshots.Remove(snapshotId);
            
            if (_currentSnapshotId == snapshotId)
                _currentSnapshotId = null;

            Console.WriteLine($"✓ Deleted snapshot #{snapshotId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to delete snapshot #{snapshotId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Compares two snapshots and shows the differences.
    /// </summary>
    public async Task CompareSnapshotsAsync(int snapshotId1, int snapshotId2)
    {
        if (!_snapshots.ContainsKey(snapshotId1) || !_snapshots.ContainsKey(snapshotId2))
        {
            Console.WriteLine("One or both snapshots not found");
            return;
        }

        Console.WriteLine($"Comparing snapshot #{snapshotId1} with snapshot #{snapshotId2}...");
        
        using var snapshot1 = await LoadSnapshotFromDump(_snapshots[snapshotId1].DumpFilePath);
        using var snapshot2 = await LoadSnapshotFromDump(_snapshots[snapshotId2].DumpFilePath);

        if (snapshot1 == null || snapshot2 == null)
        {
            Console.WriteLine("Failed to load one or both snapshots");
            return;
        }

        var comparison = new SnapshotComparison(snapshot1, snapshot2);
        comparison.GenerateReport();
    }

    private async Task<HeapSnapshot?> LoadSnapshotFromDump(string dumpPath)
    {
        try
        {
            var snapshot = new HeapSnapshot();
            snapshot.DataTarget = Microsoft.Diagnostics.Runtime.DataTarget.LoadDump(dumpPath);

            var clrInfo = snapshot.DataTarget.ClrVersions.FirstOrDefault();
            if (clrInfo == null)
                throw new InvalidOperationException("No CLR version found in dump");

            snapshot.Runtime = clrInfo.CreateRuntime();
            if (snapshot.Runtime?.Heap == null)
                throw new InvalidOperationException("Unable to access heap information");

            await HeapAnalyzer.AnalyzeHeapAsync(snapshot);
            return snapshot;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading snapshot: {ex.Message}");
            return null;
        }
    }

    private async Task SaveSnapshotMetadata(SnapshotEntry entry, HeapSnapshot snapshot)
    {
        var metadata = new SnapshotMetadata
        {
            Id = entry.Id,
            ProcessId = entry.ProcessId,
            Timestamp = entry.Timestamp,
            TotalObjects = entry.TotalObjects,
            TotalMemory = entry.TotalMemory,
            TopTypes = snapshot.TypeIndex
                .Select(kvp => new TypeSummary
                {
                    TypeName = kvp.Key,
                    Count = kvp.Value.Count,
                    TotalSize = kvp.Value.Sum(addr => (long)snapshot.Objects[addr].Size)
                })
                .OrderByDescending(t => t.TotalSize)
                .Take(20)
                .ToList()
        };

        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await File.WriteAllTextAsync(entry.MetadataFilePath, json);
    }

    private void LoadExistingSnapshots()
    {
        try
        {
            var metadataFiles = Directory.GetFiles(_snapshotsDirectory, "*_metadata.json");
            
            foreach (var metadataFile in metadataFiles)
            {
                try
                {
                    var json = File.ReadAllText(metadataFile);
                    var metadata = JsonSerializer.Deserialize<SnapshotMetadata>(json, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });

                    if (metadata != null)
                    {
                        var dumpFile = Path.Combine(_snapshotsDirectory, 
                            $"snapshot_{metadata.Id}_{metadata.Timestamp:yyyyMMdd_HHmmss}.dmp");

                        if (File.Exists(dumpFile))
                        {
                            var entry = new SnapshotEntry
                            {
                                Id = metadata.Id,
                                ProcessId = metadata.ProcessId,
                                Timestamp = metadata.Timestamp,
                                DumpFilePath = dumpFile,
                                MetadataFilePath = metadataFile,
                                TotalObjects = metadata.TotalObjects,
                                TotalMemory = metadata.TotalMemory
                            };

                            _snapshots[metadata.Id] = entry;
                            _nextSnapshotId = Math.Max(_nextSnapshotId, metadata.Id + 1);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to load snapshot metadata from {metadataFile}: {ex.Message}");
                }
            }

            if (_snapshots.Any())
            {
                Console.WriteLine($"Loaded {_snapshots.Count} existing snapshots");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading existing snapshots: {ex.Message}");
        }
    }


    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }

    public void Dispose()
    {
        // Note: We don't delete the dump files on dispose - they're meant to persist
    }
}

public record SnapshotEntry
{
    public int Id { get; init; }
    public int ProcessId { get; init; }
    public DateTime Timestamp { get; init; }
    public string DumpFilePath { get; init; } = "";
    public string MetadataFilePath { get; init; } = "";
    public long TotalObjects { get; init; }
    public long TotalMemory { get; init; }
}

public record SnapshotMetadata
{
    public int Id { get; init; }
    public int ProcessId { get; init; }
    public DateTime Timestamp { get; init; }
    public long TotalObjects { get; init; }
    public long TotalMemory { get; init; }
    public List<TypeSummary> TopTypes { get; init; } = new();
}

public record TypeSummary
{
    public string TypeName { get; init; } = "";
    public int Count { get; init; }
    public long TotalSize { get; init; }
}

/// <summary>
/// Compares two heap snapshots and shows differences.
/// </summary>
public class SnapshotComparison
{
    private readonly HeapSnapshot _snapshot1;
    private readonly HeapSnapshot _snapshot2;

    public SnapshotComparison(HeapSnapshot snapshot1, HeapSnapshot snapshot2)
    {
        _snapshot1 = snapshot1;
        _snapshot2 = snapshot2;
    }

    public void GenerateReport()
    {
        Console.WriteLine("\n=== Snapshot Comparison ===");
        
        var objDiff = _snapshot2.TotalObjects - _snapshot1.TotalObjects;
        var memDiff = _snapshot2.TotalMemory - _snapshot1.TotalMemory;
        
        Console.WriteLine($"Object count: {_snapshot1.TotalObjects:N0} → {_snapshot2.TotalObjects:N0} ({objDiff:+#,0;-#,0})");
        Console.WriteLine($"Memory usage: {FormatBytes(_snapshot1.TotalMemory)} → {FormatBytes(_snapshot2.TotalMemory)} ({FormatBytes(memDiff):+#;-#})");

        // Type comparison
        var types1 = _snapshot1.TypeIndex.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count);
        var types2 = _snapshot2.TypeIndex.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count);
        
        var allTypes = types1.Keys.Union(types2.Keys).ToList();
        var changes = allTypes
            .Select(type => new
            {
                Type = type,
                Count1 = types1.GetValueOrDefault(type, 0),
                Count2 = types2.GetValueOrDefault(type, 0)
            })
            .Where(x => x.Count1 != x.Count2)
            .OrderByDescending(x => Math.Abs(x.Count2 - x.Count1))
            .Take(20);

        Console.WriteLine("\nTop type changes:");
        Console.WriteLine("Type                           | Before | After  | Change");
        Console.WriteLine("-------------------------------|--------|--------|--------");
        
        foreach (var change in changes)
        {
            var diff = change.Count2 - change.Count1;
            Console.WriteLine($"{change.Type,-30} | {change.Count1,6:N0} | {change.Count2,6:N0} | {diff,6:+#,0;-#,0}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }
}