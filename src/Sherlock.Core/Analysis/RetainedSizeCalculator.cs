namespace Sherlock.Core.Analysis;

/// <summary>
/// Calculates retained sizes for objects in a heap snapshot using the dominator tree algorithm.
/// This class orchestrates the process of finding GC roots, building dominator tree, and calculating retained sizes.
/// </summary>
public static class RetainedSizeCalculator
{
    /// <summary>
    /// Calculates retained sizes for all objects in the snapshot using the dominator tree algorithm.
    /// This provides accurate retained size calculations by finding which objects are truly dominated.
    /// </summary>
    public static void CalculateRetainedSizes(HeapSnapshot snapshot)
    {
        try
        {
            var objectCount = snapshot._objects.Count;
            Console.WriteLine($"Calculating retained sizes for {objectCount:N0} objects using dominator tree...");
            
            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            // Step 1: Find GC roots
            var roots = GCRootFinder.FindGCRoots(snapshot);
            Console.WriteLine($"Found {roots.Count} GC root candidates in {sw.ElapsedMilliseconds}ms");
            
            // Step 2: Build dominator tree
            sw.Restart();
            var dominatorResult = DominatorTree.BuildDominatorTree(snapshot._objects, roots);
            Console.WriteLine($"Built dominator tree in {sw.ElapsedMilliseconds}ms");
            
            // Step 3: Calculate retained sizes using the dominator tree
            sw.Restart();
            DominatorTreeRetainedSizeCalculator.CalculateRetainedSizes(snapshot, dominatorResult);
            Console.WriteLine($"Completed retained size calculation in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error calculating retained sizes: {ex.Message}");
            Console.WriteLine($"Setting all retained sizes to shallow sizes as fallback");
            FallbackToShallowSizes(snapshot);
        }
    }
    
    /// <summary>
    /// Alternative method that allows using a pre-built dominator tree.
    /// Useful when you want to reuse an existing dominator tree calculation.
    /// </summary>
    /// <param name="snapshot">The heap snapshot to update</param>
    /// <param name="dominatorResult">Pre-built dominator tree result</param>
    public static void CalculateRetainedSizes(HeapSnapshot snapshot, DominatorTreeResult dominatorResult)
    {
        try
        {
            Console.WriteLine($"Calculating retained sizes using provided dominator tree...");
            DominatorTreeRetainedSizeCalculator.CalculateRetainedSizes(snapshot, dominatorResult);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error calculating retained sizes with provided dominator tree: {ex.Message}");
            FallbackToShallowSizes(snapshot);
        }
    }
    
    private static void FallbackToShallowSizes(HeapSnapshot snapshot)
    {
        foreach (var obj in snapshot._objects.Values)
        {
            obj.RetainedSize = obj.Size;
        }
        Console.WriteLine("Fallback: Set all retained sizes to shallow sizes");
    }
}