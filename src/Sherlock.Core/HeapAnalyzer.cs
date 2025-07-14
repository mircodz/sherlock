using Microsoft.Diagnostics.Runtime;
using Sherlock.Core.Analysis;

namespace Sherlock.Core;

/// <summary>
/// Handles analysis of heap data to extract object information and relationships.
/// </summary>
public static class HeapAnalyzer
{
    /// <summary>
    /// Analyzes the heap and populates the snapshot with object data.
    /// </summary>
    public static async Task AnalyzeHeapAsync(HeapSnapshot snapshot)
    {
        if (snapshot.Runtime?.Heap == null) 
        {
            Console.WriteLine("No heap available for analysis");
            return;
        }

        if (!snapshot.Runtime.Heap.CanWalkHeap)
        {
            Console.WriteLine("Heap cannot be walked - dump may be corrupted or incomplete");
            return;
        }

        Console.WriteLine("Starting heap analysis...");
        
        var segments = snapshot.Runtime.Heap.Segments.ToList();
        Console.WriteLine($"Found {segments.Count} heap segments");

        var objectCount = 0;
        var processedCount = 0;
        var skipCount = 0;

        try
        {
            foreach (var obj in snapshot.Runtime.Heap.EnumerateObjects())
            {
                objectCount++;
                
                if (objectCount % 25000 == 0)
                {
                    Console.WriteLine($"Processed {processedCount:N0} objects, skipped {skipCount:N0}");
                }

                if (ProcessObject(snapshot, obj))
                {
                    processedCount++;
                }
                else
                {
                    skipCount++;
                }

                // Stop if we hit too many errors
                if (skipCount > processedCount * 2 && objectCount > 1000)
                {
                    Console.WriteLine("Too many invalid objects - stopping analysis");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Heap enumeration failed: {ex.Message}");
            if (processedCount == 0)
            {
                await TrySegmentBasedAnalysis(snapshot);
                return;
            }
        }

        Console.WriteLine($"Analysis complete: {processedCount:N0} objects processed, {skipCount:N0} skipped");
        Console.WriteLine($"Found {snapshot._typeIndex.Count} unique types");

        if (processedCount > 0)
        {
            Console.WriteLine("Calculating retained sizes...");
            RetainedSizeCalculator.CalculateRetainedSizes(snapshot);
        }
    }
    
    private static bool ProcessObject(HeapSnapshot snapshot, ClrObject obj)
    {
        try
        {
            var type = obj.Type;
            if (type?.Name == null || obj.Address == 0)
            {
                return false;
            }

            var typeName = snapshot.StringInterner.Intern(type.Name);
            var size = obj.Size;
            
            if (size == 0)
            {
                return false;
            }

            var references = GetReferences(obj, snapshot.StringInterner);
            
            var objectInfo = new ObjectInfo
            {
                Address = obj.Address,
                TypeName = typeName,
                Size = size,
                References = references,
                Fields = new Dictionary<string, object?>(),
                Generation = 0
            };

            snapshot._objects[obj.Address] = objectInfo;

            if (!snapshot._typeIndex.ContainsKey(typeName))
                snapshot._typeIndex[typeName] = new List<ulong>();
            snapshot._typeIndex[typeName].Add(obj.Address);
            
            return true;
        }
        catch
        {
            return false;
        }
    }
    
    private static List<ObjectReference> GetReferences(ClrObject obj, StringInterner interner)
    {
        var references = new List<ObjectReference>();
        
        try
        {
            foreach (var reference in obj.EnumerateReferences())
            {
                if (reference.Address != 0 && reference.Type?.Name != null)
                {
                    references.Add(new ObjectReference
                    {
                        SourceAddress = obj.Address,
                        TargetAddress = reference.Address,
                        FieldName = interner.Intern("ref"),
                        TypeName = interner.Intern(reference.Type.Name)
                    });
                }
                
                if (references.Count > 100) break; // Limit to prevent memory issues
            }
        }
        catch
        {
            // Return what we have so far
        }
        
        return references;
    }

    private static async Task TrySegmentBasedAnalysis(HeapSnapshot snapshot)
    {
        try
        {
            Console.WriteLine("Trying segment-based analysis...");
            
            if (snapshot.Runtime?.Heap == null) return;
            
            var totalProcessed = 0;
            foreach (var segment in snapshot.Runtime.Heap.Segments.Take(5))
            {
                var segmentProcessed = 0;
                
                try
                {
                    foreach (var obj in segment.EnumerateObjects().Take(10000))
                    {
                        if (ProcessObject(snapshot, obj))
                        {
                            segmentProcessed++;
                            totalProcessed++;
                        }
                        
                        if (totalProcessed >= 50000) break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Segment analysis failed: {ex.Message}");
                }
                
                if (totalProcessed >= 50000) break;
            }
            
            Console.WriteLine($"Segment analysis processed {totalProcessed} objects");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Segment-based analysis failed: {ex.Message}");
        }
    }
}