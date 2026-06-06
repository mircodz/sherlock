using System.IO;
using System.Linq;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>Builds the high-level <see cref="DumpInfo"/> summary for a session.</summary>
public sealed class DumpInspector(DumpSession session)
{
    public DumpInfo Inspect()
    {
        ClrRuntime runtime = session.Runtime;
        ClrHeap heap = runtime.Heap;
        IDataReader reader = session.DataTarget.DataReader;

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

        long fileSize = new FileInfo(session.DumpPath).Length;
        int moduleCount = runtime.EnumerateModules().Count();
        int processId = reader.ProcessId;

        return new DumpInfo(
            DumpPath: session.DumpPath,
            FileSizeBytes: fileSize,
            ClrFlavor: session.ClrInfo.Flavor.ToString(),
            ClrVersion: session.ClrInfo.Version.ToString(),
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
