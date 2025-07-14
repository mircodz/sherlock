using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Sherlock.Core.Analysis;

/// <summary>
/// Optimized data structures for fast heap analysis and queries.
/// Provides efficient lookups, spatial indexing, and cached computations.
/// </summary>
public class OptimizedHeapStructures
{
    private readonly HeapSnapshot _snapshot;
    private readonly Lazy<SpatialIndex> _spatialIndex;
    private readonly Lazy<TypeHierarchyIndex> _typeHierarchy;
    private readonly Lazy<ReferenceGraphIndex> _referenceGraph;
    private readonly ConcurrentDictionary<string, object> _cachedQueries = new();

    public OptimizedHeapStructures(HeapSnapshot snapshot)
    {
        _snapshot = snapshot;
        _spatialIndex = new Lazy<SpatialIndex>(() => new SpatialIndex(_snapshot));
        _typeHierarchy = new Lazy<TypeHierarchyIndex>(() => new TypeHierarchyIndex(_snapshot));
        _referenceGraph = new Lazy<ReferenceGraphIndex>(() => new ReferenceGraphIndex(_snapshot));
    }

    public SpatialIndex Spatial => _spatialIndex.Value;
    public TypeHierarchyIndex TypeHierarchy => _typeHierarchy.Value;
    public ReferenceGraphIndex ReferenceGraph => _referenceGraph.Value;

    /// <summary>
    /// Gets cached result or computes and caches new result for expensive queries.
    /// </summary>
    public T GetOrCompute<T>(string key, Func<T> computation) where T : class
    {
        return (T)_cachedQueries.GetOrAdd(key, _ => computation()!);
    }

    /// <summary>
    /// Clears all cached computations to free memory.
    /// </summary>
    public void ClearCache()
    {
        _cachedQueries.Clear();
    }
}

/// <summary>
/// Spatial indexing for objects based on memory addresses and size ranges.
/// Enables fast range queries and memory layout analysis.
/// </summary>
public class SpatialIndex
{
    private readonly HeapSnapshot _snapshot;
    private readonly Dictionary<ulong, MemoryRange> _segmentRanges = new();
    private readonly SortedDictionary<ulong, List<ObjectInfo>> _addressIndex = new();
    private readonly Dictionary<int, SizeRange> _sizeRanges = new();

    public SpatialIndex(HeapSnapshot snapshot)
    {
        _snapshot = snapshot;
        BuildIndices();
    }

    private void BuildIndices()
    {
        var allObjects = _snapshot._objects.Values.OrderBy(o => o.Address).ToList();
        
        // Build address-based index for range queries
        const int bucketSize = 1000;
        for (int i = 0; i < allObjects.Count; i += bucketSize)
        {
            var bucket = allObjects.Skip(i).Take(bucketSize).ToList();
            if (bucket.Any())
            {
                _addressIndex[bucket.First().Address] = bucket;
            }
        }

        // Build size-based ranges for quick size queries
        var sizeBuckets = allObjects.GroupBy(o => GetSizeBucket((long)o.Size));
        foreach (var bucket in sizeBuckets)
        {
            _sizeRanges[bucket.Key] = new SizeRange
            {
                MinSize = bucket.Min(o => (long)o.Size),
                MaxSize = bucket.Max(o => (long)o.Size),
                Objects = bucket.ToList()
            };
        }
    }

    private static int GetSizeBucket(long size)
    {
        return size switch
        {
            < 100 => 0,
            < 1024 => 1,
            < 8192 => 2,
            < 65536 => 3,
            < 1048576 => 4,
            _ => 5
        };
    }

    /// <summary>
    /// Finds objects within a specific address range.
    /// </summary>
    public IEnumerable<ObjectInfo> GetObjectsInRange(ulong startAddress, ulong endAddress)
    {
        var startBucket = _addressIndex.Keys.LastOrDefault(k => k <= startAddress);
        if (startBucket == 0) startBucket = _addressIndex.Keys.FirstOrDefault();

        foreach (var kvp in _addressIndex.Where(kvp => kvp.Key >= startBucket))
        {
            if (kvp.Key > endAddress) break;
            
            foreach (var obj in kvp.Value)
            {
                if (obj.Address >= startAddress && obj.Address <= endAddress)
                    yield return obj;
                if (obj.Address > endAddress) break;
            }
        }
    }

    /// <summary>
    /// Finds objects within a specific size range.
    /// </summary>
    public IEnumerable<ObjectInfo> GetObjectsBySizeRange(long minSize, long maxSize)
    {
        var relevantBuckets = _sizeRanges.Values
            .Where(range => range.MaxSize >= minSize && range.MinSize <= maxSize);

        return relevantBuckets
            .SelectMany(range => range.Objects)
            .Where(obj => (long)obj.Size >= minSize && (long)obj.Size <= maxSize);
    }

    /// <summary>
    /// Gets objects near a specific address (within proximity).
    /// </summary>
    public IEnumerable<ObjectInfo> GetNearbyObjects(ulong address, ulong proximity = 4096)
    {
        return GetObjectsInRange(
            address > proximity ? address - proximity : 0,
            address + proximity);
    }

    private record MemoryRange(ulong Start, ulong End, List<ObjectInfo> Objects);
    private record SizeRange
    {
        public long MinSize { get; init; }
        public long MaxSize { get; init; }
        public List<ObjectInfo> Objects { get; init; } = new();
    }
}

/// <summary>
/// Hierarchical indexing of types for inheritance and interface queries.
/// </summary>
public class TypeHierarchyIndex
{
    private readonly HeapSnapshot _snapshot;
    private readonly Dictionary<string, TypeNode> _typeNodes = new();
    private readonly Dictionary<string, HashSet<string>> _derivedTypes = new();
    private readonly Dictionary<string, List<ObjectInfo>> _fastTypeLookup = new();

    public TypeHierarchyIndex(HeapSnapshot snapshot)
    {
        _snapshot = snapshot;
        BuildHierarchy();
    }

    private void BuildHierarchy()
    {
        // Build fast type lookup
        foreach (var kvp in _snapshot._typeIndex)
        {
            _fastTypeLookup[kvp.Key] = kvp.Value.Select(addr => _snapshot._objects[addr]).ToList();
        }

        // Build simplified type hierarchy based on naming patterns
        foreach (var typeName in _snapshot._typeIndex.Keys)
        {
            var node = new TypeNode { TypeName = typeName };
            _typeNodes[typeName] = node;

            // Simple hierarchy detection based on naming patterns
            if (typeName.Contains('+')) // Nested types
            {
                var parentType = typeName.Substring(0, typeName.LastIndexOf('+'));
                if (_typeNodes.TryGetValue(parentType, out var parent))
                {
                    node.Parent = parent;
                    parent.Children.Add(node);
                }
            }

            // Build derived types index for interfaces and base classes
            var derivedKey = GetBaseTypeName(typeName);
            if (!_derivedTypes.ContainsKey(derivedKey))
                _derivedTypes[derivedKey] = new HashSet<string>();
            _derivedTypes[derivedKey].Add(typeName);
        }
    }

    private static string GetBaseTypeName(string typeName)
    {
        // Extract base type from generic types like List<String> -> List
        var genericIndex = typeName.IndexOf('<');
        if (genericIndex > 0)
            return typeName.Substring(0, genericIndex);
        
        // Extract base type from array types like String[] -> String
        var arrayIndex = typeName.IndexOf('[');
        if (arrayIndex > 0)
            return typeName.Substring(0, arrayIndex);

        return typeName;
    }

    /// <summary>
    /// Gets all objects of a type and its derived types.
    /// </summary>
    public IEnumerable<ObjectInfo> GetObjectsIncludingDerived(string baseTypeName)
    {
        if (_derivedTypes.TryGetValue(baseTypeName, out var derivedTypes))
        {
            return derivedTypes.SelectMany(typeName => 
                _fastTypeLookup.TryGetValue(typeName, out var objects) ? objects : Enumerable.Empty<ObjectInfo>());
        }

        return _fastTypeLookup.TryGetValue(baseTypeName, out var directObjects) 
            ? directObjects 
            : Enumerable.Empty<ObjectInfo>();
    }

    /// <summary>
    /// Gets type statistics with inheritance information.
    /// </summary>
    public TypeHierarchyStats GetHierarchyStats(string typeName)
    {
        var directObjects = _fastTypeLookup.TryGetValue(typeName, out var objects) ? objects : new List<ObjectInfo>();
        var allObjects = GetObjectsIncludingDerived(typeName).ToList();

        return new TypeHierarchyStats
        {
            TypeName = typeName,
            DirectInstances = directObjects.Count,
            TotalInstancesIncludingDerived = allObjects.Count,
            DirectSize = directObjects.Sum(o => (long)o.Size),
            TotalSizeIncludingDerived = allObjects.Sum(o => (long)o.Size),
            DerivedTypes = _derivedTypes.TryGetValue(typeName, out var derived) ? derived.ToList() : new List<string>()
        };
    }

    private class TypeNode
    {
        public string TypeName { get; init; } = "";
        public TypeNode? Parent { get; set; }
        public List<TypeNode> Children { get; } = new();
    }

    public record TypeHierarchyStats
    {
        public string TypeName { get; init; } = "";
        public int DirectInstances { get; init; }
        public int TotalInstancesIncludingDerived { get; init; }
        public long DirectSize { get; init; }
        public long TotalSizeIncludingDerived { get; init; }
        public List<string> DerivedTypes { get; init; } = new();
    }
}

/// <summary>
/// Optimized reference graph for fast dominance and reachability queries.
/// </summary>
public class ReferenceGraphIndex
{
    private readonly HeapSnapshot _snapshot;
    private readonly Dictionary<ulong, List<ulong>> _outgoingRefs = new();
    private readonly Dictionary<ulong, List<ulong>> _incomingRefs = new();
    private readonly Dictionary<ulong, int> _referenceDepth = new();

    public ReferenceGraphIndex(HeapSnapshot snapshot)
    {
        _snapshot = snapshot;
        BuildGraph();
    }

    private void BuildGraph()
    {
        // Build bidirectional reference graph
        foreach (var obj in _snapshot._objects.Values)
        {
            _outgoingRefs[obj.Address] = obj.References.Select(r => r.TargetAddress).ToList();
            
            foreach (var reference in obj.References)
            {
                if (!_incomingRefs.ContainsKey(reference.TargetAddress))
                    _incomingRefs[reference.TargetAddress] = new List<ulong>();
                _incomingRefs[reference.TargetAddress].Add(obj.Address);
            }
        }

        // Compute reference depths for quick dominator approximation
        ComputeReferenceDepths();
    }

    private void ComputeReferenceDepths()
    {
        var visited = new HashSet<ulong>();
        var queue = new Queue<(ulong address, int depth)>();

        // Start from objects with no incoming references (potential roots)
        foreach (var objAddr in _snapshot._objects.Keys)
        {
            if (!_incomingRefs.ContainsKey(objAddr) || !_incomingRefs[objAddr].Any())
            {
                queue.Enqueue((objAddr, 0));
                _referenceDepth[objAddr] = 0;
            }
        }

        while (queue.Count > 0)
        {
            var (address, depth) = queue.Dequeue();
            if (visited.Contains(address)) continue;
            visited.Add(address);

            if (_outgoingRefs.TryGetValue(address, out var outgoing))
            {
                foreach (var target in outgoing)
                {
                    var newDepth = depth + 1;
                    if (!_referenceDepth.ContainsKey(target) || _referenceDepth[target] > newDepth)
                    {
                        _referenceDepth[target] = newDepth;
                        queue.Enqueue((target, newDepth));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gets all objects reachable from the given object within specified depth.
    /// </summary>
    public IEnumerable<ObjectInfo> GetReachableObjects(ulong startAddress, int maxDepth = int.MaxValue)
    {
        var visited = new HashSet<ulong>();
        var queue = new Queue<(ulong address, int depth)>();
        queue.Enqueue((startAddress, 0));

        while (queue.Count > 0)
        {
            var (address, depth) = queue.Dequeue();
            if (visited.Contains(address) || depth > maxDepth) continue;
            visited.Add(address);

            if (_snapshot._objects.TryGetValue(address, out var obj))
                yield return obj;

            if (_outgoingRefs.TryGetValue(address, out var outgoing))
            {
                foreach (var target in outgoing)
                {
                    if (!visited.Contains(target))
                        queue.Enqueue((target, depth + 1));
                }
            }
        }
    }

    /// <summary>
    /// Finds the shortest path between two objects.
    /// </summary>
    public List<ObjectInfo> FindShortestPath(ulong fromAddress, ulong toAddress)
    {
        var queue = new Queue<(ulong address, List<ulong> path)>();
        var visited = new HashSet<ulong>();
        queue.Enqueue((fromAddress, new List<ulong> { fromAddress }));

        while (queue.Count > 0)
        {
            var (address, path) = queue.Dequeue();
            if (visited.Contains(address)) continue;
            visited.Add(address);

            if (address == toAddress)
            {
                return path.Select(addr => _snapshot._objects[addr]).ToList();
            }

            if (_outgoingRefs.TryGetValue(address, out var outgoing))
            {
                foreach (var target in outgoing.Where(t => !visited.Contains(t)))
                {
                    var newPath = new List<ulong>(path) { target };
                    queue.Enqueue((target, newPath));
                }
            }
        }

        return new List<ObjectInfo>();
    }

    /// <summary>
    /// Gets objects that may be dominators (approximation based on reference depth).
    /// </summary>
    public IEnumerable<ObjectInfo> GetPotentialDominators(ulong targetAddress)
    {
        if (!_referenceDepth.TryGetValue(targetAddress, out var targetDepth))
            return Enumerable.Empty<ObjectInfo>();

        return _incomingRefs.TryGetValue(targetAddress, out var incoming)
            ? incoming.Where(addr => _referenceDepth.TryGetValue(addr, out var depth) && depth < targetDepth)
                     .Select(addr => _snapshot._objects[addr])
            : Enumerable.Empty<ObjectInfo>();
    }

    /// <summary>
    /// Gets reference statistics for an object.
    /// </summary>
    public ReferenceStats GetReferenceStats(ulong address)
    {
        var outgoingCount = _outgoingRefs.TryGetValue(address, out var outgoing) ? outgoing.Count : 0;
        var incomingCount = _incomingRefs.TryGetValue(address, out var incoming) ? incoming.Count : 0;
        var depth = _referenceDepth.TryGetValue(address, out var d) ? d : -1;

        return new ReferenceStats
        {
            Address = address,
            OutgoingReferences = outgoingCount,
            IncomingReferences = incomingCount,
            ReferenceDepth = depth,
            IsLikelyRoot = incomingCount == 0,
            IsHighlyReferenced = incomingCount > 10
        };
    }

    public record ReferenceStats
    {
        public ulong Address { get; init; }
        public int OutgoingReferences { get; init; }
        public int IncomingReferences { get; init; }
        public int ReferenceDepth { get; init; }
        public bool IsLikelyRoot { get; init; }
        public bool IsHighlyReferenced { get; init; }
    }
}