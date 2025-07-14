using Sherlock.Core;

namespace Sherlock.Testing;

/// <summary>
/// Provides a fluent API for filtering objects by type in unit tests.
/// </summary>
public class TypedObjectView
{
    private readonly HeapSnapshot _snapshot;

    internal TypedObjectView(HeapSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    /// <summary>
    /// Filters objects by type name using a predicate.
    /// First builds a lightweight type names index, then filters efficiently.
    /// </summary>
    public TypeInstanceView Where(Func<string, bool> typeFilter)
    {
        var matchingTypes = _snapshot.AvailableTypeNames.Where(typeFilter).ToList();
        return new TypeInstanceView(_snapshot, matchingTypes);
    }

    /// <summary>
    /// Filters objects where the type equals the specified type.
    /// This is highly optimized - only scans for the specific requested type.
    /// </summary>
    public TypeInstanceView Where(Type type)
    {
        return new TypeInstanceView(_snapshot, type.FullName ?? type.Name);
    }
}