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
    /// Forces garbage collection by default for consistent test results.
    /// </summary>
    /// <param name="forceGC">Whether to force garbage collection before taking snapshot (default: true)</param>
    /// <returns>A heap snapshot that can be queried for memory leaks</returns>
    public static async Task<HeapSnapshot> SnapshotAsync(bool forceGC = true)
    {
        if (forceGC)
        {
            ForceGarbageCollection();
        }
        
        var currentProcess = Process.GetCurrentProcess();
        return await HeapSnapshot.TakeSnapshotAsync(currentProcess.Id);
    }

    /// <summary>
    /// Takes a snapshot of a specific process's heap.
    /// </summary>
    /// <param name="processId">The process ID to snapshot</param>
    /// <param name="forceGC">Whether to force garbage collection before taking snapshot (default: true)</param>
    /// <returns>A heap snapshot that can be queried for memory leaks</returns>
    public static async Task<HeapSnapshot> SnapshotAsync(int processId, bool forceGC = true)
    {
        if (forceGC && processId == Process.GetCurrentProcess().Id)
        {
            ForceGarbageCollection();
        }
        
        return await HeapSnapshot.TakeSnapshotAsync(processId);
    }

    /// <summary>
    /// Takes a snapshot of the current process's heap synchronously.
    /// Note: This blocks the current thread. Prefer SnapshotAsync when possible.
    /// </summary>
    /// <param name="forceGC">Whether to force garbage collection before taking snapshot (default: true)</param>
    /// <returns>A heap snapshot that can be queried for memory leaks</returns>
    public static HeapSnapshot Snapshot(bool forceGC = true)
    {
        return SnapshotAsync(forceGC).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Takes a snapshot of a specific process's heap synchronously.
    /// Note: This blocks the current thread. Prefer SnapshotAsync when possible.
    /// </summary>
    /// <param name="processId">The process ID to snapshot</param>
    /// <param name="forceGC">Whether to force garbage collection before taking snapshot (default: true)</param>
    /// <returns>A heap snapshot that can be queried for memory leaks</returns>
    public static HeapSnapshot Snapshot(int processId, bool forceGC = true)
    {
        return SnapshotAsync(processId, forceGC).GetAwaiter().GetResult();
    }
    
    /// <summary>
    /// Forces multiple garbage collection cycles to ensure objects are collected.
    /// </summary>
    private static void ForceGarbageCollection()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        
        Thread.Sleep(100);
    }
}