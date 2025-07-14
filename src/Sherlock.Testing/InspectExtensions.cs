using Sherlock.Core;

namespace Sherlock.Testing;

/// <summary>
/// Extension methods for object inspection in unit tests.
/// </summary>
public static class InspectExtensions
{
    /// <summary>
    /// Inspects the first object of the specified type.
    /// </summary>
    public static string Inspect(this TypeInstanceView view, int index = 0, InspectOptions? options = null)
    {
        var obj = view.GetObject(index);
        if (obj == null)
        {
            return $"No object found at index {index}";
        }
        
        return ObjectInspector.Inspect(view.GetSnapshot(), obj, options);
    }
    
    /// <summary>
    /// Inspects an object at a specific address.
    /// </summary>
    public static string Inspect(this HeapSnapshot snapshot, ulong address, InspectOptions? options = null)
    {
        return ObjectInspector.Inspect(snapshot, address, options);
    }
    
    /// <summary>
    /// Inspects the first object of the specified type.
    /// </summary>
    public static string Inspect(this HeapSnapshot snapshot, Type type, int index = 0, InspectOptions? options = null)
    {
        var typeName = type.FullName ?? type.Name;
        var obj = snapshot.GetObjectsByType(typeName).Skip(index).FirstOrDefault();
        if (obj == null)
        {
            return $"No object found of type {typeName} at index {index}";
        }
        
        return ObjectInspector.Inspect(snapshot, obj, options);
    }
    
    /// <summary>
    /// Inspects an object directly.
    /// </summary>
    public static string Inspect(this ObjectInfo obj, HeapSnapshot snapshot, InspectOptions? options = null)
    {
        return ObjectInspector.Inspect(snapshot, obj, options);
    }
    
    /// <summary>
    /// Gets all objects of the specified type for inspection.
    /// </summary>
    public static IEnumerable<string> InspectAll(this TypeInstanceView view, InspectOptions? options = null, int maxItems = 5)
    {
        var snapshot = view.GetSnapshot();
        var objects = view.GetAllObjects().Take(maxItems);
        
        foreach (var obj in objects)
        {
            yield return ObjectInspector.Inspect(snapshot, obj, options);
        }
    }
}