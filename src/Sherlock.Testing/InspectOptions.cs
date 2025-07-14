namespace Sherlock.Testing;

/// <summary>
/// Configuration options for object inspection.
/// </summary>
public class InspectOptions
{
    /// <summary>
    /// Maximum depth for recursive inspection of referenced objects.
    /// </summary>
    public int MaxDepth { get; set; } = 2;
    
    /// <summary>
    /// Whether to include object fields in the inspection.
    /// </summary>
    public bool ShowFields { get; set; } = true;
    
    /// <summary>
    /// Whether to include object references in the inspection.
    /// </summary>
    public bool ShowReferences { get; set; } = true;
    
    /// <summary>
    /// Whether to include allocation information (if available).
    /// </summary>
    public bool ShowAllocationInfo { get; set; } = false;
    
    /// <summary>
    /// Whether to include size information.
    /// </summary>
    public bool ShowSizes { get; set; } = true;
    
    /// <summary>
    /// Whether to include GC root information in debug format.
    /// </summary>
    public bool ShowGCRoots { get; set; } = true;
    
    /// <summary>
    /// The format for the inspection output.
    /// </summary>
    public InspectFormat Format { get; set; } = InspectFormat.Detailed;
    
    /// <summary>
    /// Maximum number of references to show (prevents excessive output).
    /// </summary>
    public int MaxReferences { get; set; } = 10;
    
    /// <summary>
    /// Maximum number of fields to show (prevents excessive output).
    /// </summary>
    public int MaxFields { get; set; } = 20;
}

/// <summary>
/// Available formats for object inspection output.
/// </summary>
public enum InspectFormat
{
    /// <summary>
    /// Single line summary format.
    /// </summary>
    Compact,
    
    /// <summary>
    /// Multi-line detailed format with tree structure.
    /// </summary>
    Detailed,
    
    /// <summary>
    /// JSON format for programmatic use.
    /// </summary>
    Json,
    
    /// <summary>
    /// Debug format similar to profiler command output.
    /// </summary>
    Debug
}