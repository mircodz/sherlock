using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Tracing.Parsers;
using System.Diagnostics.Tracing;

namespace Sherlock.Core;

/// <summary>
/// Handles the capture and creation of heap snapshots from .NET processes.
/// </summary>
public static class SnapshotCapture
{
    /// <summary>
    /// Takes a heap snapshot of the specified process.
    /// </summary>
    /// <param name="processId">The process ID to snapshot</param>
    /// <returns>A HeapSnapshot containing the captured data</returns>
    public static async Task<HeapSnapshot> TakeSnapshotAsync(int processId)
    {
        var snapshot = new HeapSnapshot();
        await CaptureHeapAsync(snapshot, processId);
        return snapshot;
    }
    
    private static async Task CaptureHeapAsync(HeapSnapshot snapshot, int processId)
    {
        snapshot.ProcessId = processId;
        snapshot.SnapshotTime = DateTime.UtcNow;

        try
        {
            Console.WriteLine("Preparing for memory dump...");
            var client = new DiagnosticsClient(processId);
            
            // Skip heap stabilization to avoid hangs
            Console.WriteLine("Proceeding directly to memory dump...");

            Console.WriteLine("Creating memory dump...");
            var dumpPath = Path.GetTempFileName() + ".dmp";

            // Use normal heap dump
            client.WriteDump(DumpType.WithHeap, dumpPath);
            Console.WriteLine($"Dump created at: {dumpPath}");
            
            // Store the dump path for the snapshot manager
            snapshot._tempDumpPath = dumpPath;

            Console.WriteLine("Loading dump for analysis...");
            snapshot.DataTarget = DataTarget.LoadDump(dumpPath);

            Console.WriteLine($"Found {snapshot.DataTarget.ClrVersions.Count()} CLR versions");
            var clrInfo = snapshot.DataTarget.ClrVersions.FirstOrDefault();
            if (clrInfo == null)
                throw new InvalidOperationException("No CLR version found in dump");
                
            snapshot.Runtime = clrInfo.CreateRuntime();

            if (snapshot.Runtime == null)
                throw new InvalidOperationException("Unable to create CLR runtime from dump");

            if (snapshot.Runtime.Heap == null)
                throw new InvalidOperationException("Unable to access heap information");

            Console.WriteLine("Heap snapshot captured successfully");
            // Defer analysis until explicitly requested

            // Don't delete the dump file - let SnapshotManager handle it
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to capture heap snapshot: {ex.Message}", ex);
        }
    }
    
    private static async Task TryStabilizeHeap(DiagnosticsClient client)
    {
        try
        {
            Console.WriteLine("Stabilizing heap before dump...");
            
            // Give the runtime a moment to settle any ongoing operations
            await Task.Delay(1000);
            
            // Attempt to nudge the GC through EventPipe monitoring
            try
            {
                var providers = new EventPipeProvider[]
                {
                    new EventPipeProvider("Microsoft-Windows-DotNETRuntime",
                        EventLevel.Informational,
                        (long)ClrTraceEventParser.Keywords.GC)
                };
                
                using var session = client.StartEventPipeSession(providers, false, 1);
                await Task.Delay(500);
                session.Stop();
                
                Console.WriteLine("Heap stabilization completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Note: EventPipe stabilization unavailable: {ex.Message}");
            }
            
            await Task.Delay(500);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Heap stabilization failed: {ex.Message}");
        }
    }

}