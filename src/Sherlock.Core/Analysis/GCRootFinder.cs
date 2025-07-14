namespace Sherlock.Core.Analysis;

/// <summary>
/// Finds GC roots in a heap snapshot using various strategies.
/// </summary>
public static class GCRootFinder
{
    /// <summary>
    /// Finds GC roots in the given heap snapshot.
    /// </summary>
    /// <param name="snapshot">The heap snapshot to analyze</param>
    /// <returns>List of addresses that are GC roots</returns>
    public static List<ulong> FindGCRoots(HeapSnapshot snapshot)
    {
        var roots = new List<ulong>();
        
        if (snapshot.Runtime?.Heap == null)
        {
            Console.WriteLine("Warning: No heap available for GC root enumeration");
            return FindRootsByReferenceCounting(snapshot);
        }

        try
        {
            Console.WriteLine("Enumerating actual GC roots from CLR...");
            var rootCount = 0;
            var validRoots = 0;

            // Enumerate actual GC roots from the CLR
            foreach (var root in snapshot.Runtime.Heap.EnumerateRoots())
            {
                rootCount++;
                
                // Only include roots that point to objects we have in our snapshot
                if (root.Object != 0 && snapshot._objects.ContainsKey(root.Object))
                {
                    roots.Add(root.Object);
                    validRoots++;
                    
                    // Populate GC root path information for the object
                    var obj = snapshot._objects[root.Object];
                    var rootPath = new GCRootPath
                    {
                        RootKind = root.RootKind.ToString(),
                        RootAddress = root.Address,
                        ObjectAddress = root.Object,
                        RootName = GetRootName(root)
                    };
                    
                    obj.GCRootPaths.Add(rootPath);
                    
                    if (validRoots <= 10) // Log first 10 for debugging
                    {
                        Console.WriteLine($"  GC Root: {root.RootKind} -> 0x{root.Object:X} ({obj.TypeName})");
                    }
                }
            }
            
            Console.WriteLine($"Found {rootCount} total GC roots, {validRoots} point to tracked objects");
            
            // If we didn't find any GC roots, fall back to reference counting
            if (roots.Count == 0)
            {
                Console.WriteLine("No GC roots found, using reference counting fallback...");
                return FindRootsByReferenceCounting(snapshot);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error enumerating GC roots: {ex.Message}");
            Console.WriteLine("Falling back to reference counting...");
            return FindRootsByReferenceCounting(snapshot);
        }

        return roots.Distinct().ToList();
    }

    private static string GetRootName(Microsoft.Diagnostics.Runtime.ClrRoot root)
    {
        // Try to get a meaningful name for the root
        try
        {
            var kindName = root.RootKind.ToString();
            return $"{kindName}_{root.Address:X}";
        }
        catch
        {
            return $"Root_{root.Address:X}";
        }
    }

    private static List<ulong> FindRootsByReferenceCounting(HeapSnapshot snapshot)
    {
        var roots = new List<ulong>();
        var incomingRefCounts = new Dictionary<ulong, int>();

        // Count incoming references
        foreach (var obj in snapshot._objects.Values)
        {
            incomingRefCounts[obj.Address] = 0;
        }

        foreach (var obj in snapshot._objects.Values)
        {
            foreach (var reference in obj.References)
            {
                if (incomingRefCounts.ContainsKey(reference.TargetAddress))
                {
                    incomingRefCounts[reference.TargetAddress]++;
                }
            }
        }

        // Objects with no incoming references are potential GC roots
        foreach (var kvp in incomingRefCounts)
        {
            if (kvp.Value == 0)
            {
                roots.Add(kvp.Key);
            }
        }

        Console.WriteLine($"Reference counting found {roots.Count} potential roots");
        return roots;
    }
}