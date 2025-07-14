using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core;

public static class ClrMDExtensions
{
    /// <summary>
    /// Enumerates types with constructed method tables in all modules.
    /// This reimplements the functionality that was removed in ClrMD 2.0.
    /// </summary>
    public static IEnumerable<ClrType> EnumerateTypes(this ClrHeap heap)
    {
        if (heap is null)
            throw new ArgumentNullException(nameof(heap));

        // The ClrHeap actually doesn't know anything about 'types' in the strictest sense, that's
        // all tracked by the runtime. First, grab the runtime object:
        ClrRuntime runtime = heap.Runtime;

        // Now we loop through every module and grab every constructed MethodTable
        foreach (ClrModule module in runtime.EnumerateModules())
        {
            foreach ((ulong mt, int _) in module.EnumerateTypeDefToMethodTableMap())
            {
                // Now try to construct a type for mt. This may fail if the type was only partially
                // loaded, dump inconsistency, and in some odd corner cases like transparent proxies:
                ClrType? type = runtime.GetTypeByMethodTable(mt);

                if (type != null)
                    yield return type;
            }
        }
    }

    /// <summary>
    /// Check if the heap can be walked. This property was removed in ClrMD 2.0.
    /// </summary>
    public static bool CanWalkHeap(this ClrHeap heap)
    {
        try
        {
            // Try to get the first object to see if heap walking works
            using var enumerator = heap.EnumerateObjects().GetEnumerator();
            return true; // If we can create the enumerator, heap can be walked
        }
        catch
        {
            return false;
        }
    }
}