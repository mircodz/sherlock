using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>Builds the high-level <see cref="DumpInfo"/> summary for a session.</summary>
public sealed class DumpInspector
{
    private readonly DumpSession _session;

    public DumpInspector(DumpSession session) => _session = session;

    public DumpInfo Inspect()
    {
        ClrRuntime runtime = _session.Runtime;
        ClrHeap heap = runtime.Heap;
        IDataReader reader = _session.DataTarget.DataReader;

        ulong totalHeap = 0;
        int heapCount = 0;
        foreach (ClrSubHeap subHeap in heap.SubHeaps)
        {
            heapCount++;
            foreach (ClrSegment segment in subHeap.Segments)
                totalHeap += segment.Length;
        }

        long fileSize = new FileInfo(_session.DumpPath).Length;
        int moduleCount = runtime.EnumerateModules().Count();
        int processId = reader.ProcessId;

        return new DumpInfo(
            DumpPath: _session.DumpPath,
            FileSizeBytes: fileSize,
            ClrFlavor: _session.ClrInfo.Flavor.ToString(),
            ClrVersion: _session.ClrInfo.Version.ToString(),
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
