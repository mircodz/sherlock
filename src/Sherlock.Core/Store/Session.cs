using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace Sherlock.Core.Store;

/// <summary>How a session came to be.</summary>
public enum SessionKind
{
    Run,
    Collect,
    Import,
    Crash,
}

/// <summary>
/// A workspace grouping under <c>~/.sherlock/&lt;id&gt;/</c>: one launched run (or a
/// one-off collect/import) that owns any heap snapshots taken from it, plus its
/// side artifacts (stdout/stderr log, allocation profile) in the same directory.
/// </summary>
public sealed class Session
{
    public string Id { get; set; } = "";
    public SessionKind Kind { get; set; }
    public string Dir { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string? SourceProcess { get; set; }
    public int? SourcePid { get; set; }

    /// <summary>Captured stdout/stderr, if this is a run.</summary>
    public string? LogPath { get; set; }

    /// <summary>Allocation profile (folded stacks), if the run was profiled.</summary>
    public string? AllocationsPath { get; set; }

    public List<SnapshotEntry> Snapshots { get; set; } = [];

    [JsonIgnore]
    public bool HasAllocations => AllocationsPath is not null && File.Exists(AllocationsPath);
}
