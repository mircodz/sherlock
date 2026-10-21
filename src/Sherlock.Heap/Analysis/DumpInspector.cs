using System.IO;
using System.Linq;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Heap.Analysis;

/// <summary>Builds the high-level <see cref="DumpInfo"/> summary.</summary>
public sealed class DumpInspector(HeapDump dump)
{
    public DumpInfo Inspect()
    {
        ClrRuntime runtime = dump.Runtime;
        ClrHeap heap = runtime.Heap;
        IDataReader reader = dump.DataTarget.DataReader;

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

        long fileSize = new FileInfo(dump.DumpPath).Length;
        int moduleCount = runtime.EnumerateModules().Count();
        int processId = reader.ProcessId;

        return new DumpInfo(
            DumpPath: dump.DumpPath,
            FileSizeBytes: fileSize,
            ClrFlavor: dump.ClrInfo.Flavor.ToString(),
            ClrVersion: dump.ClrInfo.Version.ToString(),
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
