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
    /// <summary>True if the underlying dump file still exists on disk.</summary>
    [JsonIgnore]
    public bool Exists => File.Exists(Path);
}
