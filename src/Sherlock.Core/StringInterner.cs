using System.Collections.Concurrent;

namespace Sherlock.Core;

/// <summary>
/// Thread-safe string interner that reduces memory usage by ensuring only one copy
/// of each unique string is kept in memory.
/// </summary>
public class StringInterner
{
    private readonly ConcurrentDictionary<string, string> _internedStrings = new();
    
    /// <summary>
    /// Interns a string, returning the canonical instance if it already exists,
    /// or storing and returning the new string if it doesn't.
    /// </summary>
    /// <param name="str">The string to intern</param>
    /// <returns>The interned string instance</returns>
    public string Intern(string str)
    {
        if (string.IsNullOrEmpty(str))
            return str;
            
        return _internedStrings.GetOrAdd(str, str);
    }
    
    /// <summary>
    /// Gets the number of unique strings currently interned.
    /// </summary>
    public int Count => _internedStrings.Count;
    
    /// <summary>
    /// Clears all interned strings. Use with caution as this may cause
    /// existing references to become invalid.
    /// </summary>
    public void Clear()
    {
        _internedStrings.Clear();
    }
    
    /// <summary>
    /// Gets memory usage statistics for the interned strings.
    /// </summary>
    public InternStatistics GetStatistics()
    {
        var totalBytes = 0L;
        var count = 0;
        
        foreach (var str in _internedStrings.Values)
        {
            totalBytes += str.Length * sizeof(char);
            count++;
        }
        
        return new InternStatistics
        {
            UniqueStringCount = count,
            TotalMemoryBytes = totalBytes,
            AverageStringLength = count > 0 ? totalBytes / (count * sizeof(char)) : 0
        };
    }
}

/// <summary>
/// Statistics about interned strings.
/// </summary>
public record InternStatistics
{
    public int UniqueStringCount { get; init; }
    public long TotalMemoryBytes { get; init; }
    public long AverageStringLength { get; init; }
}