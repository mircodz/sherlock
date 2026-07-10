using System;
using System.IO;
using System.Text.Json.Serialization;

namespace Sherlock.Core.Store;

/// <summary>A single heap dump belonging to a <see cref="Session"/>.</summary>
/// <param name="Id">Short id, e.g. <c>s3</c>.</param>
/// <param name="Path">Absolute path to the dump file.</param>
/// <param name="Owned">True if Sherlock owns the file (so removal may delete it).</param>
public sealed record SnapshotEntry(
    string Id,
    string Path,
    bool Owned,
    string? Label,
    DateTimeOffset CreatedAt,
    long SizeBytes)
{
    /// <summary>Why the snapshot was taken — a trigger (e.g. <c>throw:Foo</c>), <c>crash</c>, etc. Null for a manual snapshot.</summary>
    public string? Reason { get; init; }

    /// <summary>Whether an allocation profile is bundled with this snapshot (persisted for display).</summary>
    public bool HasAllocations => AllocationsPath is not null;

    /// <summary>Whether an allocation-provenance sidecar is bundled with this snapshot (persisted for display).</summary>
    public bool HasCorrelation => CorrelationPath is not null;

    /// <summary>
    /// The snapshot's bundle folder — holds <c>heap.dmp</c> (this <see cref="Path"/>) plus the
    /// coherently-captured <c>allocations.tsv</c> / <c>correlation.tsv</c> when profiled.
    /// </summary>
    [JsonIgnore]
    public string Dir => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    /// <summary>The allocation profile captured with this snapshot, if any.</summary>
    [JsonIgnore]
    public string? AllocationsPath => Bundled("allocations.tsv");

    /// <summary>The correlation sidecar captured with this snapshot, if any.</summary>
    [JsonIgnore]
    public string? CorrelationPath => Bundled("correlation.tsv");

    private string? Bundled(string name)
    {
        string p = System.IO.Path.Combine(Dir, name);
        return File.Exists(p) ? p : null;
    }

    /// <summary>True if the underlying dump file still exists on disk.</summary>
    [JsonIgnore]
    public bool Exists => File.Exists(Path);
}
