namespace Sherlock.Core.Diagnostics;

/// <summary>How serious a finding is; findings sort by this.</summary>
public enum FindingSeverity
{
    High,
    Warning,
    Info,
}

/// <summary>
/// One issue the doctor spotted. Title and Detail are self-contained and plain (no markup) so the
/// REPL can style them and an agent can read them; the structured fields and <see cref="NextCommand"/>
/// let you drill in - the doctor points at the next command to run, it doesn't run it for you.
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
