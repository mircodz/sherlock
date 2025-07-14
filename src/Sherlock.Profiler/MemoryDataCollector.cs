using System.Collections.Concurrent;
using Sherlock.Core;

namespace Sherlock.Profiler;

public class MemoryDataCollector
{
    private static readonly Lazy<MemoryDataCollector> _instance = new(() => new MemoryDataCollector());
    public static MemoryDataCollector Instance => _instance.Value;

    private readonly ConcurrentQueue<AllocationInfo> _allocations = new();
    private readonly ConcurrentQueue<HeapStatistics> _heapStats = new();
    private readonly object _lockObject = new();
    private volatile bool _isCollecting = false;

    private MemoryDataCollector() { }

    public void StartCollection()
    {
        lock (_lockObject)
        {
            _isCollecting = true;
            _allocations.Clear();
            _heapStats.Clear();
        }
    }

    public void StopCollection()
    {
        lock (_lockObject)
        {
            _isCollecting = false;
        }
    }

    public void RecordAllocation(AllocationInfo allocation)
    {
        if (_isCollecting)
        {
            _allocations.Enqueue(allocation);
            
        }
    }

    public void RecordHeapStats(HeapStatistics stats)
    {
        if (_isCollecting)
        {
            _heapStats.Enqueue(stats);
        }
    }

    public IReadOnlyList<AllocationInfo> GetAllocations()
    {
        return _allocations.ToArray();
    }

    public IReadOnlyList<HeapStatistics> GetHeapStatistics()
    {
        return _heapStats.ToArray();
    }

    public MemoryAnalysisReport GenerateReport()
    {
        var allocations = GetAllocations();
        var heapStats = GetHeapStatistics();

        var totalAllocations = allocations.Sum(a => (long)a.AllocationAmount);
        var allocationsByType = allocations
            .GroupBy(a => a.TypeName)
            .Select(g => new TypeAllocationSummary
            {
                TypeName = g.Key,
                Count = g.Count(),
                TotalSize = g.Sum(a => (long)a.AllocationAmount),
                Allocations = g.ToList()
            })
            .OrderByDescending(s => s.TotalSize)
            .ToList();

        var latestHeapStats = heapStats.LastOrDefault();

        return new MemoryAnalysisReport
        {
            TotalAllocations = (ulong)totalAllocations,
            AllocationCount = allocations.Count,
            AllocationsByType = allocationsByType,
            LatestHeapStatistics = latestHeapStats,
            HeapStatisticsHistory = heapStats.ToList()
        };
    }

    public void Clear()
    {
        lock (_lockObject)
        {
            while (_allocations.TryDequeue(out _)) { }
            while (_heapStats.TryDequeue(out _)) { }
        }
    }

}


public record HeapStatistics
{
    public DateTime Timestamp { get; init; }
    public int ProcessId { get; init; }
    public ulong GenerationSize0 { get; init; }
    public ulong GenerationSize1 { get; init; }
    public ulong GenerationSize2 { get; init; }
    public ulong GenerationSize3 { get; init; }
    public ulong TotalHeapSize { get; init; }
    public ulong FinalizationPromotedSize { get; init; }
}

public record TypeAllocationSummary
{
    public string TypeName { get; init; } = "";
    public int Count { get; init; }
    public long TotalSize { get; init; }
    public List<AllocationInfo> Allocations { get; init; } = new();
}

public record MemoryAnalysisReport
{
    public ulong TotalAllocations { get; init; }
    public int AllocationCount { get; init; }
    public List<TypeAllocationSummary> AllocationsByType { get; init; } = new();
    public HeapStatistics? LatestHeapStatistics { get; init; }
    public List<HeapStatistics> HeapStatisticsHistory { get; init; } = new();
}