using Microsoft.Diagnostics.NETCore.Client;

namespace Sherlock.Core.Collection;

/// <summary>How much of the target process to capture.</summary>
public enum DumpKind
{
    /// <summary>Smallest: threads + stacks, little heap.</summary>
    Mini,
    /// <summary>Threads plus the managed heap — the sweet spot for analysis.</summary>
    Heap,
    /// <summary>Triage dump: minimal PII, useful for sharing.</summary>
    Triage,
    /// <summary>Everything, including the full native address space.</summary>
    Full,
}

/// <summary>
/// Collects a memory dump from a live .NET process over the diagnostics IPC
/// channel. The runtime writes the dump in-process (its own <c>createdump</c>),
/// producing a minidump that <see cref="DumpSession"/> can open directly.
/// </summary>
public static class DumpCollector
{
    /// <summary>
    /// Writes a dump of process <paramref name="pid"/> to <paramref name="outputPath"/>
    /// (or a generated temp path) and returns the file path written.
    /// </summary>
    /// <exception cref="DumpAnalysisException">Collection failed (bad pid, permissions, unsupported).</exception>
    public static string Collect(int pid, DumpKind kind, string? outputPath = null)
    {
        string path = outputPath ?? DefaultPath(pid);

        try
        {
            var client = new DiagnosticsClient(pid);
            client.WriteDump(Map(kind), path, logDumpGeneration: false);
        }
        catch (Exception ex) when (ex is not DumpAnalysisException)
        {
            throw new DumpAnalysisException(
                $"Could not collect a dump from process {pid}: {ex.Message} " +
                "(is it a .NET process owned by you, and still running?)", ex);
        }

        return path;
    }

    private static DumpType Map(DumpKind kind) => kind switch
    {
        DumpKind.Mini => DumpType.Normal,
        DumpKind.Heap => DumpType.WithHeap,
        DumpKind.Triage => DumpType.Triage,
        DumpKind.Full => DumpType.Full,
        _ => DumpType.WithHeap,
    };

    private static string DefaultPath(int pid)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(Path.GetTempPath(), $"sherlock-{pid}-{stamp}.dmp");
    }
}
