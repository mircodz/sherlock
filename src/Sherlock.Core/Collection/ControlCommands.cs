namespace Sherlock.Core.Collection;

/// <summary>
/// The control-channel command verbs and event names, in one place so the C# server and the
/// native profiler agree. These are the payloads carried in <c>REQ</c>/<c>EVENT</c> frames;
/// see the framing in <see cref="ProfilerControlServer"/> and src/native/src/control/channel.cpp.
/// </summary>
public static class ControlCommands
{
    /// <summary>Liveness check.</summary>
    public const string Ping = "ping";

    /// <summary>Force a GC, then emit the live-object → allocation-stack correlation sidecar.</summary>
    public const string EmitCorrelation = "emit-correlation";

    /// <summary>Flush the aggregated allocation profile to disk now (rather than at exit).</summary>
    public const string FlushAllocations = "flush-allocations";

    /// <summary>Arm a snapshot trigger (<c>call:/alloc:/gc:/throw:</c>) on a live target.</summary>
    public const string ArmTrigger = "arm-trigger";

    /// <summary>Return the number of GCs seen so far (for snapshot drift detection).</summary>
    public const string GcCount = "gc-count";
}

/// <summary>Event names pushed by the profiler over the channel (in <c>EVENT</c> frames).</summary>
public static class ControlEvents
{
    /// <summary>An armed snapshot trigger fired; the payload is its display name.</summary>
    public const string SnapshotTrigger = "snapshot-trigger";
}
