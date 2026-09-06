using System;
using System.Collections.Generic;
using System.Linq;

namespace Sherlock.Core.Collection;

public enum ProfilerLogLevel
{
    Trace,
    Info,
    Warning,
    Error,
    Off,
}

public sealed record RunOptions
{
    public required IReadOnlyList<string> Command { get; init; }
    public bool Profile { get; init; }
    public bool Correlate { get; init; }
    public bool CollectChildren { get; init; }
    public bool ExperimentalGcBarrier { get; init; }
    public string? SnapshotOn { get; init; }
    public string? OutputDirectory { get; init; }
    public string? ProfilerPath { get; init; }
    public ProfilerLogLevel ProfilerLogLevel { get; init; } = ProfilerLogLevel.Warning;
    public bool NeedsProfiler => Profile || Correlate || CollectChildren || ExperimentalGcBarrier || SnapshotOn is not null || ProfilerPath is not null;
    public bool SnapshotOnExit =>
        SnapshotOn?.Split(
            [';', ','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(value => value.Equals("exit", StringComparison.OrdinalIgnoreCase)) == true;
    public bool UseGcBarrier => Correlate && (ExperimentalGcBarrier || SnapshotOnExit);

    public void Validate()
    {
        if (Command is null || Command.Count == 0 || string.IsNullOrWhiteSpace(Command[0]))
        {
            throw new ArgumentException("Run command cannot be empty.", nameof(Command));
        }
        if (!Enum.IsDefined(ProfilerLogLevel))
        {
            throw new ArgumentException("Profiler log level must be trace, info, warning, error, or off.", nameof(ProfilerLogLevel));
        }
        if (ExperimentalGcBarrier && !Correlate)
        {
            throw new ArgumentException("--experimental-gc-barrier requires --correlate.", nameof(ExperimentalGcBarrier));
        }
    }
}
