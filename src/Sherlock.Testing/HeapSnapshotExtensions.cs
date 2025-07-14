using Sherlock.Core;

namespace Sherlock.Testing;

/// <summary>
/// Extension methods for HeapSnapshot to enable unit testing functionality.
/// </summary>
public static class HeapSnapshotExtensions
{
    /// <summary>
    /// Creates a typed view for filtering objects by type for unit test assertions.
    /// </summary>
    public static TypedObjectView Types(this HeapSnapshot snapshot)
    {
        return new TypedObjectView(snapshot);
    }
}