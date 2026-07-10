using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace Sherlock.Core.Store;

/// <summary>How a workspace came to be.</summary>
public enum SessionKind
{
    Run,
    Collect,
    Import,
    Crash,
}

/// <summary>A .NET process seen within a workspace, and the snapshots taken from it.</summary>
public sealed class ProcessRecord
{
    public int Pid { get; set; }
    public string? Name { get; set; }
    public string? Exec { get; set; }
    public bool IsRoot { get; set; }

    /// <summary>Exit-time run-level allocation profile for this process, if it was profiled.</summary>
    public string? AllocationsPath { get; set; }

    public List<SnapshotEntry> Snapshots { get; set; } = [];

    [JsonIgnore]
    public bool HasAllocations => AllocationsPath is not null && File.Exists(AllocationsPath);
}

/// <summary>
/// A workspace under <c>~/.sherlock/&lt;id&gt;/</c>: one launched run (or a one-off collect/import),
/// spanning one or more processes — each owning the heap snapshots taken from it. Side artifacts
/// (log, per-process allocation profiles, snapshot bundles) live in the same directory.
/// </summary>
public sealed class Session
{
    public string Id { get; set; } = "";
    public SessionKind Kind { get; set; }
    public string Dir { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The launch command (for a run), or the source name (for a collect/import).</summary>
    public string? Command { get; set; }

    /// <summary>Captured stdout/stderr, if this is a run.</summary>
    public string? LogPath { get; set; }

    public List<ProcessRecord> Processes { get; set; } = [];

    /// <summary>Every snapshot across all processes (flat view, for lookups).</summary>
    [JsonIgnore]
    public IEnumerable<SnapshotEntry> Snapshots => Processes.SelectMany(p => p.Snapshots);

    /// <summary>The launched (root) process, or the first one seen.</summary>
    [JsonIgnore]
    public ProcessRecord? Root => Processes.FirstOrDefault(p => p.IsRoot) ?? Processes.FirstOrDefault();

    [JsonIgnore]
    public bool HasAllocations => Processes.Any(p => p.HasAllocations);

    /// <summary>Finds or creates the process record for a pid.</summary>
    public ProcessRecord GetOrAddProcess(int pid, string? name, bool isRoot = false)
    {
        ProcessRecord? existing = Processes.FirstOrDefault(p => p.Pid == pid);
        if (existing is not null)
        {
            existing.Name ??= name;
            return existing;
        }

        var record = new ProcessRecord { Pid = pid, Name = name, IsRoot = isRoot };
        Processes.Add(record);
        return record;
    }
}
