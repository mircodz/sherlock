using Sherlock.Core;

namespace Sherlock.Testing;

/// <summary>
/// Provides assertions for a specific type or multiple types in unit tests.
/// </summary>
public class TypeInstanceView
{
    private readonly HeapSnapshot _snapshot;
    private readonly List<string> _typeNames;

    internal TypeInstanceView(HeapSnapshot snapshot, string typeName)
    {
        _snapshot = snapshot;
        _typeNames = new List<string> { typeName };
    }

    internal TypeInstanceView(HeapSnapshot snapshot, List<string> typeNames)
    {
        _snapshot = snapshot;
        _typeNames = typeNames;
    }

    /// <summary>
    /// Gets the count of instances for this type or all matching types.
    /// Uses lazy evaluation - only builds type index when needed.
    /// </summary>
    public int Instances
    {
        get
        {
            int totalCount = 0;
            foreach (var typeName in _typeNames)
            {
                totalCount += _snapshot.GetTypeCount(typeName);
            }
            return totalCount;
        }
    }
    
    /// <summary>
    /// Gets the first object of this type for inspection.
    /// </summary>
    public ObjectInfo? FirstObject
    {
        get
        {
            foreach (var typeName in _typeNames)
            {
                var obj = _snapshot.GetObjectsByType(typeName).FirstOrDefault();
                if (obj != null) return obj;
            }
            return null;
        }
    }
    
    /// <summary>
    /// Gets the object at the specified index for this type.
    /// </summary>
    public ObjectInfo? GetObject(int index)
    {
        foreach (var typeName in _typeNames)
        {
            var obj = _snapshot.GetObjectsByType(typeName).Skip(index).FirstOrDefault();
            if (obj != null) return obj;
        }
        return null;
    }

    /// <summary>
    /// Assertion helper for unit tests.
    /// </summary>
    public TypeInstanceAssertion Should()
    {
        var description = _typeNames.Count == 1 ? _typeNames[0] : $"{_typeNames.Count} matching types";
        return new TypeInstanceAssertion(Instances, description);
    }
    
    /// <summary>
    /// Gets the underlying snapshot for inspection operations.
    /// </summary>
    internal HeapSnapshot GetSnapshot() => _snapshot;
    
    /// <summary>
    /// Gets all objects of the matching types.
    /// </summary>
    internal IEnumerable<ObjectInfo> GetAllObjects()
    {
        foreach (var typeName in _typeNames)
        {
            foreach (var obj in _snapshot.GetObjectsByType(typeName))
            {
                yield return obj;
            }
        }
    }
}