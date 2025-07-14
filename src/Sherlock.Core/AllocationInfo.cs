namespace Sherlock.Core;

public record AllocationInfo
{
    public DateTime Timestamp { get; init; }
    public int ProcessId { get; init; }
    public int ThreadId { get; init; }
    public string ThreadName { get; init; } = "";
    public ulong AllocationAmount { get; init; }
    public string AllocationKind { get; init; } = "";
    public string TypeName { get; init; } = "";
    public string StackTrace { get; init; } = "";
    public List<StackFrame> ParsedStackTrace { get; init; } = new();
    public string AllocationSite { get; init; } = "";
    
    /// <summary>
    /// Gets the total size for heap snapshot correlation
    /// </summary>
    public ulong TotalSize => AllocationAmount;
}

/// <summary>
/// Represents a single frame in an allocation stack trace.
/// </summary>
public record StackFrame
{
    public string MethodName { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string FileName { get; init; } = "";
    public int LineNumber { get; init; }
    public string Module { get; init; } = "";
    
    public override string ToString()
    {
        var location = "";
        if (!string.IsNullOrEmpty(FileName) && LineNumber > 0)
        {
            location = $" in {Path.GetFileName(FileName)}:line {LineNumber}";
        }
        else if (!string.IsNullOrEmpty(Module))
        {
            location = $" in {Module}";
        }
        
        return $"{ClassName}.{MethodName}(){location}";
    }
}