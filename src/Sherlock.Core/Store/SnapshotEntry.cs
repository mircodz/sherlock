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
    /// <summary>Trigger (<c>throw:Foo</c>), <c>crash</c>, etc. Null for a manual snapshot.</summary>
    public string? Reason { get; init; }

    /// <summary>Whether the bundled provenance carries per-object correlation (a <c>--correlate</c> capture).</summary>
    public bool HasCorrelation { get; init; }

    /// <summary>Whether a provenance container (allocation profile ± correlation) is bundled.</summary>
    public bool HasAllocations => ProvenancePath is not null;

    /// <summary>The bundle folder: <c>heap.dmp</c> (this <see cref="Path"/>) plus <c>provenance.slab</c>.</summary>
    [JsonIgnore]
    public string Dir => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    /// <summary>The allocation-provenance container, if bundled. Read via <c>ProvenanceReader</c>.</summary>
    [JsonIgnore]
    public string? ProvenancePath => Bundled("provenance.slab");

    private string? Bundled(string name)
    {
        string p = System.IO.Path.Combine(Dir, name);
        return File.Exists(p) ? p : null;
    }

    /// <summary>True if the underlying dump file still exists on disk.</summary>
    [JsonIgnore]
    public bool Exists => File.Exists(Path);
}
