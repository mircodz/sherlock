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
    /// </summary>
    public int Instances
    {
        get
        {
            int totalCount = 0;
            foreach (var typeName in _typeNames)
            {
                if (_snapshot.TypeIndex.TryGetValue(typeName, out var addresses))
                {
                    totalCount += addresses.Count;
                }
            }
            return totalCount;
        }
    }

    /// <summary>
    /// Assertion helper for unit tests.
    /// </summary>
    public TypeInstanceAssertion Should()
    {
        var description = _typeNames.Count == 1 ? _typeNames[0] : $"{_typeNames.Count} matching types";
        return new TypeInstanceAssertion(Instances, description);
    }
}