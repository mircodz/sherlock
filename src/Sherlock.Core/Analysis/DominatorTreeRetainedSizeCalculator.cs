namespace Sherlock.Core.Analysis;

/// <summary>
/// Calculates retained sizes using a pre-built dominator tree.
/// This class takes a dominator tree as input and computes accurate retained sizes.
/// </summary>
public static class DominatorTreeRetainedSizeCalculator
{
    /// <summary>
    /// Calculates retained sizes using a pre-built dominator tree.
    /// </summary>
    /// <param name="snapshot">The heap snapshot to update with retained sizes</param>
    /// <param name="dominatorResult">Pre-built dominator tree result</param>
    public static void CalculateRetainedSizes(HeapSnapshot snapshot, DominatorTreeResult dominatorResult)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"Calculating retained sizes using dominator tree for {snapshot._objects.Count:N0} objects...");
            
            var retainedSizes = new Dictionary<ulong, ulong>();
            var visited = new HashSet<ulong>();
            
            // Calculate retained sizes bottom-up using post-order traversal
            foreach (var address in snapshot._objects.Keys)
            {
                if (!visited.Contains(address))
                {
                    CalculateRetainedSizeRecursive(address, snapshot._objects, dominatorResult.DominatorTree, retainedSizes, visited);
                }
            }

            // Update object retained sizes
            var updatedObjects = 0;
            foreach (var obj in snapshot._objects.Values)
            {
                if (retainedSizes.TryGetValue(obj.Address, out var retainedSize))
                {
                    obj.RetainedSize = retainedSize;
                    updatedObjects++;
                }
                else
                {
                    obj.RetainedSize = obj.Size; // Fallback to shallow size
                }
            }
            
            Console.WriteLine($"Calculated retained sizes for {updatedObjects} of {snapshot._objects.Count} objects in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error calculating retained sizes with dominator tree: {ex.Message}");
            throw;
        }
    }

    private static ulong CalculateRetainedSizeRecursive(
        ulong address, 
        Dictionary<ulong, ObjectInfo> objects,
        Dictionary<ulong, List<ulong>> dominatorTree, 
        Dictionary<ulong, ulong> retainedSizes, 
        HashSet<ulong> visited)
    {
        if (visited.Contains(address) || retainedSizes.ContainsKey(address))
        {
            return retainedSizes.TryGetValue(address, out var existingSize) ? existingSize : 0;
        }

        visited.Add(address);

        if (!objects.TryGetValue(address, out var obj))
        {
            return 0;
        }

        ulong totalSize = obj.Size; // Start with shallow size

        // Add sizes of all dominated objects
        if (dominatorTree.TryGetValue(address, out var dominatedObjects))
        {
            foreach (var dominatedAddr in dominatedObjects)
            {
                totalSize += CalculateRetainedSizeRecursive(dominatedAddr, objects, dominatorTree, retainedSizes, visited);
            }
        }

        retainedSizes[address] = totalSize;
        return totalSize;
    }
}