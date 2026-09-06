namespace Sherlock.Core.Diagnostics;

/// <summary>How serious a finding is; findings sort by this.</summary>
public enum FindingSeverity
{
    High,
    Warning,
    Info,
}

/// <summary>
/// A diagnostic with plain-text Title and Detail. NextCommand suggests further inspection without running it.
/// </summary>
public sealed record Finding(
    FindingSeverity Severity,
    string Category,
    string Title,
    string Detail)
{
    /// <summary>A representative object address, when the finding points at one.</summary>
    public ulong? Address { get; init; }

    /// <summary>The type most implicated, if any.</summary>
    public string? Type { get; init; }

    public long? Bytes { get; init; }
    public long? Count { get; init; }

    /// <summary>The command that drills into this finding, e.g. <c>gcroot 0x1234</c>.</summary>
    public string? NextCommand { get; init; }
}
