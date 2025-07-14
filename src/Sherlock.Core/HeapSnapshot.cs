using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.Analysis;

namespace Sherlock.Core;

/// <summary>
/// Represents a snapshot of the managed heap at a specific point in time.
/// Contains object information, type indexing, and analysis results.
/// </summary>
public class HeapSnapshot : IDisposable
{
    public DataTarget? DataTarget { get; set; }
    public ClrRuntime? Runtime { get; set; }
    internal readonly Dictionary<ulong, ObjectInfo> _objects = new();
    internal readonly Dictionary<string, List<ulong>> _typeIndex = new();
    
    /// <summary>
    /// Gets the type index for external access (used by testing framework).
    /// This will build a lightweight type names index if needed for predicate filtering.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ulong>> TypeIndex 
    {
        get
        {
            EnsureTypeNamesIndexBuilt();
            return _typeIndex.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<ulong>)kvp.Value.AsReadOnly());
        }
    }
    
    /// <summary>
    /// Gets all available type names without scanning for their addresses.
    /// Useful for predicate-based filtering.
    /// </summary>
    public IReadOnlySet<string> AvailableTypeNames
    {
        get
        {
            EnsureTypeNamesIndexBuilt();
            return _availableTypeNames;
        }
    }
    
    /// <summary>
    /// Gets read-only access to all objects in the snapshot.
    /// </summary>
    public IReadOnlyDictionary<ulong, ObjectInfo> Objects => _objects.AsReadOnly();
    
    /// <summary>
    /// Gets the temporary dump file path, if any.
    /// </summary>
    public string? TempDumpPath => _tempDumpPath;
    internal readonly StringInterner StringInterner = new();
    internal readonly Dictionary<ulong, AllocationInfo> _allocationData = new();
    private readonly Lazy<OptimizedHeapStructures> _optimizedStructures;
    internal string? _tempDumpPath;
    private bool _disposed;
    
    public DateTime SnapshotTime { get; internal set; }
    public int ProcessId { get; internal set; }
    public long TotalObjects => IsAnalyzed ? _objects.Count : GetQuickObjectCount();
    public long TotalMemory => IsAnalyzed ? _objects.Values.Sum(o => (long)o.Size) : GetQuickTotalMemory();
    public bool IsAnalyzed => _objects.Any();
    
    /// <summary>
    /// Tracks which types have been fully scanned to avoid duplicate work.
    /// </summary>
    private readonly HashSet<string> _scannedTypes = new();
    
    /// <summary>
    /// Lightweight index of just type names (without addresses) for predicate filtering.
    /// </summary>
    private readonly HashSet<string> _availableTypeNames = new();
    private bool _typeNamesIndexBuilt = false;
    
    /// <summary>
    /// Gets the count of objects of a specific type without full analysis.
    /// Only scans for the requested type if not already done.
    /// </summary>
    public int GetTypeCount(string typeName)
    {
        EnsureTypeScanned(typeName);
        return _typeIndex.TryGetValue(typeName, out var addresses) ? addresses.Count : 0;
    }
    
    /// <summary>
    /// Ensures the specified type has been scanned and indexed.
    /// 
    /// Performance Strategy:
    /// - Exact type queries (e.g., typeof(MyClass)) only scan for that specific type
    /// - Predicate queries (e.g., t => t.Contains("String")) first build a lightweight type names index
    /// - No artificial limits - processes all objects but only for requested types
    /// - Progressive caching - once a type is scanned, subsequent queries are instant
    /// </summary>
    private void EnsureTypeScanned(string typeName)
    {
        if (_scannedTypes.Contains(typeName) || Runtime?.Heap == null) return;
        
        ScanForType(typeName);
        _scannedTypes.Add(typeName);
    }
    
    /// <summary>
    /// Builds a lightweight index of available type names without addresses.
    /// Much faster than full type indexing and enables efficient predicate filtering.
    /// </summary>
    private void EnsureTypeNamesIndexBuilt()
    {
        if (_typeNamesIndexBuilt || Runtime?.Heap == null) return;
        
        try
        {
            foreach (var obj in Runtime.Heap.EnumerateObjects())
            {
                if (obj.Type?.Name != null)
                {
                    _availableTypeNames.Add(obj.Type.Name);
                }
            }
            _typeNamesIndexBuilt = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error building type names index: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Scans the heap for objects of a specific type only.
    /// Much more efficient than scanning all types.
    /// </summary>
    private void ScanForType(string targetTypeName)
    {
        try
        {
            var addresses = new List<ulong>();
            
            foreach (var obj in Runtime.Heap.EnumerateObjects())
            {
                if (obj.Type?.Name == targetTypeName)
                {
                    addresses.Add(obj.Address);
                }
            }
            
            if (addresses.Count > 0)
            {
                var internedTypeName = StringInterner.Intern(targetTypeName);
                _typeIndex[internedTypeName] = addresses;
            }
            else
            {
                // Even if no objects found, mark as scanned to avoid re-scanning
                var internedTypeName = StringInterner.Intern(targetTypeName);
                _typeIndex[internedTypeName] = new List<ulong>();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error scanning for type {targetTypeName}: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Gets optimized data structures for advanced analysis.
    /// </summary>
    public OptimizedHeapStructures Optimized => _optimizedStructures.Value;

    public HeapSnapshot()
    {
        _optimizedStructures = new Lazy<OptimizedHeapStructures>(() => new OptimizedHeapStructures(this));
    }

    /// <summary>
    /// Creates a heap snapshot of the specified process.
    /// </summary>
    public static async Task<HeapSnapshot> TakeSnapshotAsync(int processId)
    {
        return await SnapshotCapture.TakeSnapshotAsync(processId);
    }

    /// <summary>
    /// Gets all objects of a specific type.
    /// Only scans for the requested type if not already done.
    /// </summary>
    public IEnumerable<ObjectInfo> GetObjectsByType(string typeName)
    {
        EnsureTypeScanned(typeName);
        if (_typeIndex.TryGetValue(typeName, out var addresses))
        {
            // If objects are analyzed, return them directly
            if (IsAnalyzed)
            {
                return addresses.Select(addr => _objects[addr]);
            }
            
            // Otherwise, analyze objects on demand
            return addresses.Select(addr => GetOrAnalyzeObject(addr)).Where(obj => obj != null)!;
        }
        return Enumerable.Empty<ObjectInfo>();
    }
    
    /// <summary>
    /// Gets or analyzes a single object on demand.
    /// </summary>
    private ObjectInfo? GetOrAnalyzeObject(ulong address)
    {
        if (_objects.TryGetValue(address, out var existing))
            return existing;
            
        // Analyze single object on demand
        if (Runtime?.Heap != null)
        {
            try
            {
                var clrObj = Runtime.Heap.GetObject(address);
                if (clrObj.Type?.Name != null)
                {
                    var objInfo = new ObjectInfo
                    {
                        Address = address,
                        TypeName = StringInterner.Intern(clrObj.Type.Name),
                        Size = clrObj.Size,
                        Generation = 0,
                        References = GetReferencesForObject(clrObj),
                        Fields = GetFieldsForObject(clrObj)
                    };
                    
                    _objects[address] = objInfo;
                    return objInfo;
                }
            }
            catch { /* Ignore errors for individual objects */ }
        }
        
        return null;
    }
    
    /// <summary>
    /// Extracts field values from a CLR object.
    /// </summary>
    private Dictionary<string, object?> GetFieldsForObject(Microsoft.Diagnostics.Runtime.ClrObject clrObj)
    {
        var fields = new Dictionary<string, object?>();
        
        try
        {
            if (clrObj.Type == null) return fields;
            
            foreach (var field in clrObj.Type.Fields)
            {
                try
                {
                    var fieldName = StringInterner.Intern(field.Name ?? "<unknown>");
                    var fieldValue = GetFieldValue(clrObj, field);
                    fields[fieldName] = fieldValue;
                }
                catch
                {
                    // Skip fields that can't be read
                }
            }
        }
        catch
        {
            // Return empty if we can't read fields
        }
        
        return fields;
    }
    
    /// <summary>
    /// Extracts references from a CLR object with proper field mapping.
    /// </summary>
    private List<ObjectReference> GetReferencesForObject(Microsoft.Diagnostics.Runtime.ClrObject clrObj)
    {
        var references = new List<ObjectReference>();
        
        try
        {
            if (clrObj.Type == null) return references;
            
            // Get references through fields to get proper field names
            foreach (var field in clrObj.Type.Fields)
            {
                try
                {
                    if (field.Type != null && !field.Type.IsPrimitive && field.Type.Name != "System.String")
                    {
                        var refObj = clrObj.ReadObjectField(field.Name);
                        if (refObj.IsValid && refObj.Type?.Name != null)
                        {
                            references.Add(new ObjectReference
                            {
                                SourceAddress = clrObj.Address,
                                TargetAddress = refObj.Address,
                                FieldName = StringInterner.Intern(field.Name),
                                TypeName = StringInterner.Intern(refObj.Type.Name)
                            });
                        }
                    }
                }
                catch
                {
                    // Skip fields that can't be read
                }
                
                if (references.Count > 20) break; // Limit to prevent memory issues
            }
        }
        catch
        {
            // Return what we have so far
        }
        
        return references;
    }
    
    /// <summary>
    /// Gets the value of a specific field from a CLR object.
    /// </summary>
    private object? GetFieldValue(Microsoft.Diagnostics.Runtime.ClrObject clrObj, Microsoft.Diagnostics.Runtime.ClrInstanceField field)
    {
        try
        {
            if (field.Type == null) return null;
            
            // Handle primitive types
            if (field.Type.IsPrimitive)
            {
                return field.Type.Name switch
                {
                    "System.Boolean" => clrObj.ReadField<bool>(field.Name),
                    "System.Byte" => clrObj.ReadField<byte>(field.Name),
                    "System.SByte" => clrObj.ReadField<sbyte>(field.Name),
                    "System.Int16" => clrObj.ReadField<short>(field.Name),
                    "System.UInt16" => clrObj.ReadField<ushort>(field.Name),
                    "System.Int32" => clrObj.ReadField<int>(field.Name),
                    "System.UInt32" => clrObj.ReadField<uint>(field.Name),
                    "System.Int64" => clrObj.ReadField<long>(field.Name),
                    "System.UInt64" => clrObj.ReadField<ulong>(field.Name),
                    "System.Single" => clrObj.ReadField<float>(field.Name),
                    "System.Double" => clrObj.ReadField<double>(field.Name),
                    "System.Char" => clrObj.ReadField<char>(field.Name),
                    _ => $"<primitive: {field.Type.Name}>"
                };
            }
            
            // Handle strings specially
            if (field.Type.Name == "System.String")
            {
                var stringObj = clrObj.ReadObjectField(field.Name);
                return stringObj.IsValid ? stringObj.AsString() : null;
            }
            
            // Handle reference types
            var refObj = clrObj.ReadObjectField(field.Name);
            if (refObj.IsValid && refObj.Type != null)
            {
                return $"<object: {refObj.Type.Name} @ 0x{refObj.Address:X}>";
            }
            
            return null;
        }
        catch
        {
            return $"<error reading {field.Name}>";
        }
    }

    /// <summary>
    /// Gets an object by its address.
    /// </summary>
    public ObjectInfo? GetObject(ulong address)
    {
        return _objects.TryGetValue(address, out var obj) ? obj : null;
    }

    /// <summary>
    /// Gets all incoming references to an object.
    /// </summary>
    public IEnumerable<ObjectReference> GetIncomingReferences(ulong objectAddress)
    {
        return _objects.Values
            .Where(obj => obj.References.Any(r => r.TargetAddress == objectAddress))
            .SelectMany(obj => obj.References.Where(r => r.TargetAddress == objectAddress)
                .Select(r => new ObjectReference
                {
                    TargetAddress = objectAddress,
                    SourceAddress = obj.Address,
                    FieldName = r.FieldName,
                    TypeName = r.TypeName
                }));
    }

    /// <summary>
    /// Gets statistics for a specific type.
    /// </summary>
    public TypeStatistics GetTypeStatistics(string typeName)
    {
        var objects = GetObjectsByType(typeName).ToList();
        if (!objects.Any())
            return new TypeStatistics { TypeName = typeName };

        return new TypeStatistics
        {
            TypeName = typeName,
            InstanceCount = objects.Count,
            TotalSize = objects.Sum(o => (long)o.Size),
            TotalRetainedSize = objects.Sum(o => (long)o.RetainedSize),
            AverageSize = objects.Average(o => (double)o.Size),
            GenerationDistribution = objects.GroupBy(o => o.Generation)
                .ToDictionary(g => g.Key, g => g.Count()),
            LargestInstances = objects.OrderByDescending(o => o.Size).Take(10).ToList()
        };
    }

    /// <summary>
    /// Fast query for objects within a specific size range using optimized structures.
    /// </summary>
    public IEnumerable<ObjectInfo> GetObjectsBySizeRange(long minSize, long maxSize)
    {
        return Optimized.Spatial.GetObjectsBySizeRange(minSize, maxSize);
    }

    /// <summary>
    /// Fast query for objects near a specific address using spatial indexing.
    /// </summary>
    public IEnumerable<ObjectInfo> GetNearbyObjects(ulong address, ulong proximity = 4096)
    {
        return Optimized.Spatial.GetNearbyObjects(address, proximity);
    }

    /// <summary>
    /// Gets all objects reachable from a specific object within depth limit.
    /// </summary>
    public IEnumerable<ObjectInfo> GetReachableObjects(ulong startAddress, int maxDepth = 5)
    {
        return Optimized.ReferenceGraph.GetReachableObjects(startAddress, maxDepth);
    }

    /// <summary>
    /// Finds the shortest reference path between two objects.
    /// </summary>
    public List<ObjectInfo> FindReferencePath(ulong fromAddress, ulong toAddress)
    {
        return Optimized.ReferenceGraph.FindShortestPath(fromAddress, toAddress);
    }

    /// <summary>
    /// Gets type hierarchy statistics including derived types.
    /// </summary>
    public TypeHierarchyIndex.TypeHierarchyStats GetTypeHierarchyStats(string typeName)
    {
        return Optimized.TypeHierarchy.GetHierarchyStats(typeName);
    }


    /// <summary>
    /// Performs heap analysis if not already done.
    /// </summary>
    public async Task AnalyzeAsync()
    {
        if (Runtime?.Heap == null)
        {
            Console.WriteLine("No heap available for analysis");
            return;
        }

        try
        {
            if (!IsAnalyzed)
            {
                Console.WriteLine("Starting heap analysis...");
                await HeapAnalyzer.AnalyzeHeapAsync(this);
                Console.WriteLine($"Analysis complete: {TotalObjects:N0} objects analyzed");
            }
            else
            {
                Console.WriteLine("Heap already analyzed");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Analysis failed: {ex.Message}");
            throw;
        }
    }



    /// <summary>
    /// Generates a comprehensive analysis report of the heap.
    /// </summary>
    public HeapAnalysisReport GenerateReport()
    {
        var typeStats = _typeIndex.Keys
            .Select(GetTypeStatistics)
            .OrderByDescending(s => s.TotalRetainedSize)
            .ToList();

        var generationStats = _objects.Values
            .GroupBy(o => o.Generation)
            .ToDictionary(g => g.Key, g => new GenerationStatistics
            {
                Generation = g.Key,
                ObjectCount = g.Count(),
                TotalSize = g.Sum(o => (long)o.Size),
                TotalRetainedSize = g.Sum(o => (long)o.RetainedSize)
            });

        return new HeapAnalysisReport
        {
            SnapshotTime = SnapshotTime,
            ProcessId = ProcessId,
            TotalObjects = TotalObjects,
            TotalMemory = TotalMemory,
            TypeStatistics = typeStats,
            GenerationStatistics = generationStats.Values.ToList(),
            LargestObjects = _objects.Values
                .OrderByDescending(o => o.RetainedSize)
                .Take(50).ToList()
        };
    }

    /// <summary>
    /// Gets object count quickly from heap segments without full analysis.
    /// </summary>
    private long GetQuickObjectCount()
    {
        try
        {
            if (Runtime?.Heap == null) return 0;
            
            // Use the heap's built-in object enumeration count if available
            var count = 0L;
            foreach (var segment in Runtime.Heap.Segments.Take(10)) // Limit to avoid hanging
            {
                try
                {
                    // Just count objects in segments without processing them
                    foreach (var obj in segment.EnumerateObjects().Take(100000))
                    {
                        if (obj.Type != null) count++;
                        if (count > 10000000) break; // Safety limit
                    }
                }
                catch
                {
                    break;
                }
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets total memory quickly from heap segments without full analysis.
    /// </summary>
    private long GetQuickTotalMemory()
    {
        try
        {
            if (Runtime?.Heap == null) return 0;
            
            return Runtime.Heap.Segments.Sum(s => (long)s.Length);
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        Runtime?.Dispose();
        DataTarget?.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Information about a single object in the heap.
/// </summary>
public record ObjectInfo
{
    public ulong Address { get; init; }
    public string TypeName { get; init; } = "";
    public ulong Size { get; init; }
    public ulong RetainedSize { get; set; }
    public int Generation { get; init; }
    public List<ObjectReference> References { get; init; } = new();
    public Dictionary<string, object?> Fields { get; init; } = new();
    public List<GCRootPath> GCRootPaths { get; set; } = new();
    public AllocationInfo? AllocationInfo { get; set; }
}

/// <summary>
/// Represents a reference from one object to another.
/// </summary>
public record ObjectReference
{
    public ulong TargetAddress { get; init; }
    public ulong SourceAddress { get; init; }
    public string FieldName { get; init; } = "";
    public string TypeName { get; init; } = "";
}

/// <summary>
/// Represents a path from a GC root to an object.
/// </summary>
public record GCRootPath
{
    public string RootKind { get; init; } = "";
    public ulong RootAddress { get; init; }
    public ulong ObjectAddress { get; init; }
    public string RootName { get; init; } = "";
}

/// <summary>
/// Statistics about objects of a specific type.
/// </summary>
public record TypeStatistics
{
    public string TypeName { get; init; } = "";
    public int InstanceCount { get; init; }
    public long TotalSize { get; init; }
    public long TotalRetainedSize { get; init; }
    public double AverageSize { get; init; }
    public Dictionary<int, int> GenerationDistribution { get; init; } = new();
    public List<ObjectInfo> LargestInstances { get; init; } = new();
}

/// <summary>
/// Statistics about objects in a specific generation.
/// </summary>
public record GenerationStatistics
{
    public int Generation { get; init; }
    public int ObjectCount { get; init; }
    public long TotalSize { get; init; }
    public long TotalRetainedSize { get; init; }
}

/// <summary>
/// Comprehensive report of heap analysis results.
/// </summary>
public record HeapAnalysisReport
{
    public DateTime SnapshotTime { get; init; }
    public int ProcessId { get; init; }
    public long TotalObjects { get; init; }
    public long TotalMemory { get; init; }
    public List<TypeStatistics> TypeStatistics { get; init; } = new();
    public List<GenerationStatistics> GenerationStatistics { get; init; } = new();
    public List<ObjectInfo> LargestObjects { get; init; } = new();
}

