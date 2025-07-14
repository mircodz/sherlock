using System.Diagnostics;
using Sherlock.Core;

namespace Sherlock.Testing;

/// <summary>
/// Main entry point for Sherlock memory profiling and heap analysis.
/// </summary>
public static class Sherlock
{
    /// <summary>
    /// Takes a snapshot of the current process's heap for analysis in unit tests.
    /// </summary>
    /// <returns>A heap snapshot that can be queried for memory leaks</returns>
    public static async Task<HeapSnapshot> SnapshotAsync()
    {
        var currentProcess = Process.GetCurrentProcess();
        return await HeapSnapshot.TakeSnapshotAsync(currentProcess.Id);
    }

    /// <summary>
    /// Takes a snapshot of a specific process's heap.
    /// </summary>
    /// <param name="processId">The process ID to snapshot</param>
    /// <returns>A heap snapshot that can be queried for memory leaks</returns>
    public static async Task<HeapSnapshot> SnapshotAsync(int processId)
    {
        return await HeapSnapshot.TakeSnapshotAsync(processId);
    }

    /// <summary>
    /// Takes a snapshot of the current process's heap synchronously.
    /// Note: This blocks the current thread. Prefer SnapshotAsync when possible.
    /// </summary>
    /// <returns>A heap snapshot that can be queried for memory leaks</returns>
    public static HeapSnapshot Snapshot()
    {
        return SnapshotAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Takes a snapshot of a specific process's heap synchronously.
    /// Note: This blocks the current thread. Prefer SnapshotAsync when possible.
    /// </summary>
    /// <param name="processId">The process ID to snapshot</param>
    /// <returns>A heap snapshot that can be queried for memory leaks</returns>
    public static HeapSnapshot Snapshot(int processId)
    {
        return SnapshotAsync(processId).GetAwaiter().GetResult();
    }
}