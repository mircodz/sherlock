using System.IO;
using System.Linq;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>Builds the high-level <see cref="DumpInfo"/> summary.</summary>
public sealed class DumpInspector(Snapshot snapshot)
{
    public DumpInfo Inspect()
    {
        ClrRuntime runtime = snapshot.Runtime;
        ClrHeap heap = runtime.Heap;
        IDataReader reader = snapshot.DataTarget.DataReader;

        ulong totalHeap = 0;
        int heapCount = 0;
        foreach (ClrSubHeap subHeap in heap.SubHeaps)
        {
            heapCount++;
            foreach (ClrSegment segment in subHeap.Segments)
            {
                totalHeap += segment.Length;
            }
        }

        long fileSize = new FileInfo(snapshot.DumpPath).Length;
        int moduleCount = runtime.EnumerateModules().Count();
        int processId = reader.ProcessId;

        return new DumpInfo(
            DumpPath: snapshot.DumpPath,
            FileSizeBytes: fileSize,
            ClrFlavor: snapshot.ClrInfo.Flavor.ToString(),
            ClrVersion: snapshot.ClrInfo.Version.ToString(),
            Architecture: reader.Architecture.ToString(),
            Platform: reader.TargetPlatform.ToString(),
            ProcessId: processId == 0 ? null : processId,
            ServerGc: heap.IsServer,
            HeapCount: heapCount,
            TotalHeapBytes: totalHeap,
            ThreadCount: runtime.Threads.Length,
            ModuleCount: moduleCount);
    }
}
