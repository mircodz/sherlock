using Sherlock.Testing;
using Sherlock.Core;

namespace Sherlock.Tests;

/// <summary>
/// Helper utilities for memory leak tests.
/// </summary>
public static class TestUtilities
{
    private static readonly List<string> _dumpFilesToCleanup = new();
    private static readonly object _cleanupLock = new();
    /// <summary>
    /// Forces multiple garbage collection cycles to ensure objects are collected.
    /// </summary>
    public static void ForceGarbageCollection()
    {
        // Force multiple GC cycles to ensure cleanup
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        
        // Give GC a moment to complete
        Thread.Sleep(100);
    }

    /// <summary>
    /// Takes a heap snapshot and analyzes it for testing.
    /// </summary>
    public static async Task<HeapSnapshot> TakeAnalyzedSnapshotAsync()
    {
        var snapshot = await Testing.Sherlock.SnapshotAsync();
        await snapshot.AnalyzeAsync();
        
        // Register for cleanup if there's a temp dump file
        if (!string.IsNullOrEmpty(snapshot.TempDumpPath))
        {
            RegisterDumpFileForCleanup(snapshot.TempDumpPath);
        }
        
        return snapshot;
    }

    /// <summary>
    /// Creates a disposable scope that ensures cleanup after test.
    /// </summary>
    public static IDisposable CreateTestScope(Action cleanup)
    {
        return new TestScope(cleanup);
    }

    private class TestScope : IDisposable
    {
        private readonly Action _cleanup;
        private bool _disposed;

        public TestScope(Action cleanup)
        {
            _cleanup = cleanup;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cleanup();
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Registers a dump file for cleanup after tests complete.
    /// </summary>
    public static void RegisterDumpFileForCleanup(string dumpFilePath)
    {
        lock (_cleanupLock)
        {
            if (!_dumpFilesToCleanup.Contains(dumpFilePath))
            {
                _dumpFilesToCleanup.Add(dumpFilePath);
            }
        }
    }

    /// <summary>
    /// Cleans up all registered dump files.
    /// </summary>
    public static void CleanupDumpFiles()
    {
        lock (_cleanupLock)
        {
            foreach (var dumpFile in _dumpFilesToCleanup)
            {
                try
                {
                    if (File.Exists(dumpFile))
                    {
                        File.Delete(dumpFile);
                        Console.WriteLine($"Cleaned up dump file: {dumpFile}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to cleanup dump file {dumpFile}: {ex.Message}");
                }
            }
            _dumpFilesToCleanup.Clear();
        }
    }

    /// <summary>
    /// Sets up a finalizer to cleanup dump files when the AppDomain shuts down.
    /// </summary>
    static TestUtilities()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupDumpFiles();
        AppDomain.CurrentDomain.DomainUnload += (_, _) => CleanupDumpFiles();
    }
}