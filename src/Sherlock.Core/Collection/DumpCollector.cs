using System;
using System.IO;
using Microsoft.Diagnostics.NETCore.Client;

namespace Sherlock.Core.Collection;

/// <summary>How much of the target process to capture.</summary>
public enum DumpKind
{
    /// <summary>Smallest: threads + stacks, little heap.</summary>
    Mini,
    /// <summary>Threads plus the managed heap, the sweet spot for analysis.</summary>
    Heap,
    /// <summary>Triage dump: minimal PII, useful for sharing.</summary>
    Triage,
    /// <summary>Everything, including the full native address space.</summary>
    Full,
}

/// <summary>
/// Collects a memory dump from a live .NET process over the diagnostics IPC channel. The runtime
/// writes the dump in-process (its own <c>createdump</c>), producing a minidump that
/// <see cref="DumpSession"/> can open directly.
/// </summary>
public static class DumpCollector
{
    private static readonly Lazy<string> PrivateTempDirectory = new(CreatePrivateTempDirectory);

    /// <summary>
    /// Writes a dump of process <paramref name="pid"/> to <paramref name="outputPath"/>
    /// (or a generated temp path) and returns the file path written.
    /// </summary>
    /// <exception cref="DumpAnalysisException">Collection failed (bad pid, permissions, unsupported).</exception>
    public static string Collect(int pid, DumpKind kind, string? outputPath = null)
    {
        bool ownsPath = outputPath is null;
        string path = outputPath ?? DefaultPath(pid);

        try
        {
            var client = new DiagnosticsClient(pid);
            client.WriteDump(Map(kind), path, logDumpGeneration: false);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                throw new IOException("the runtime did not produce a non-empty dump");
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (ex is not DumpAnalysisException)
        {
            if (ownsPath)
            {
                TryDelete(path);
            }
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
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        return Path.Combine(
            PrivateTempDirectory.Value,
            $"sherlock-{pid}-{stamp}-{Guid.NewGuid():N}.dmp");
    }

    private static string CreatePrivateTempDirectory()
    {
        string path = Directory.CreateTempSubdirectory("sherlock-dumps-").FullName;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
