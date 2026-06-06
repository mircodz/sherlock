using System;
using System.IO;
using System.Text.Json.Serialization;

namespace Sherlock.Core.Store;

/// <summary>How a snapshot came to be in the library.</summary>
public enum SnapshotOrigin
{
    Import,
    Collect,
    Run,
    Crash,
}

/// <summary>A catalogued dump in the workspace library.</summary>
/// <param name="Id">Short workspace id, e.g. <c>s3</c>.</param>
/// <param name="Path">Absolute path to the dump file.</param>
/// <param name="Owned">True if Sherlock owns the file (so <c>rm</c> may delete it).</param>
public sealed record SnapshotEntry(
    string Id,
    string Path,
    bool Owned,
    string? Label,
    DateTimeOffset CreatedAt,
    string? SourceProcess,
    int? SourcePid,
    SnapshotOrigin Origin,
    long SizeBytes)
{
    /// <summary>True if the underlying dump file still exists on disk.</summary>
    [JsonIgnore]
    public bool Exists => File.Exists(Path);
}
