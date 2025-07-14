using System.Text;
using System.Text.Json;
using Sherlock.Core;

namespace Sherlock.Testing;

/// <summary>
/// Provides object inspection functionality for unit tests.
/// </summary>
public static class ObjectInspector
{
    /// <summary>
    /// Inspects an object and returns a formatted string representation.
    /// </summary>
    public static string Inspect(HeapSnapshot snapshot, ObjectInfo obj, InspectOptions? options = null)
    {
        options ??= new InspectOptions();
        
        return options.Format switch
        {
            InspectFormat.Compact => InspectCompact(obj),
            InspectFormat.Json => InspectJson(snapshot, obj, options),
            InspectFormat.Debug => InspectDebug(snapshot, obj, options),
            _ => InspectDetailed(snapshot, obj, options)
        };
    }
    
    /// <summary>
    /// Inspects an object at a specific address.
    /// </summary>
    public static string Inspect(HeapSnapshot snapshot, ulong address, InspectOptions? options = null)
    {
        var obj = snapshot.GetObject(address);
        if (obj == null)
        {
            return $"Object not found at address 0x{address:X}";
        }
        
        return Inspect(snapshot, obj, options);
    }
    
    private static string InspectCompact(ObjectInfo obj)
    {
        var sb = new StringBuilder();
        sb.Append($"{obj.TypeName} @ 0x{obj.Address:X}");
        
        if (obj.Size > 0)
        {
            sb.Append($" ({FormatBytes(obj.Size)}");
            if (obj.RetainedSize > obj.Size)
            {
                sb.Append($", retained: {FormatBytes(obj.RetainedSize)}");
            }
            sb.Append(")");
        }
        
        if (obj.Fields.Any())
        {
            sb.Append(" Fields: ");
            var fieldSummary = obj.Fields.Take(3).Select(f => $"{f.Key}={FormatFieldValue(f.Value, compact: true)}");
            sb.Append(string.Join(", ", fieldSummary));
            if (obj.Fields.Count > 3)
            {
                sb.Append($" (+{obj.Fields.Count - 3} more)");
            }
        }
        
        return sb.ToString();
    }
    
    private static string InspectDetailed(HeapSnapshot snapshot, ObjectInfo obj, InspectOptions options, int currentDepth = 0, HashSet<ulong>? visited = null)
    {
        visited ??= new HashSet<ulong>();
        
        if (visited.Contains(obj.Address) || currentDepth > options.MaxDepth)
        {
            return currentDepth > options.MaxDepth ? 
                $"{"".PadLeft(currentDepth * 2)}... (max depth reached)" :
                $"{"".PadLeft(currentDepth * 2)}... (circular reference)";
        }
        
        visited.Add(obj.Address);
        var indent = "".PadLeft(currentDepth * 2);
        var sb = new StringBuilder();
        
        // Header
        sb.AppendLine($"{indent}{obj.TypeName} @ 0x{obj.Address:X}");
        
        // Size information
        if (options.ShowSizes)
        {
            sb.AppendLine($"{indent}├─ Size: {FormatBytes(obj.Size)}");
            if (obj.RetainedSize > obj.Size)
            {
                sb.AppendLine($"{indent}├─ Retained Size: {FormatBytes(obj.RetainedSize)}");
            }
            sb.AppendLine($"{indent}├─ Generation: {obj.Generation}");
        }
        
        // Allocation information
        if (options.ShowAllocationInfo && obj.AllocationInfo != null)
        {
            sb.AppendLine($"{indent}├─ Allocated: {obj.AllocationInfo.Timestamp:HH:mm:ss.fff} on thread {obj.AllocationInfo.ThreadId}");
        }
        
        // Fields
        if (options.ShowFields && obj.Fields.Any())
        {
            var hasMore = options.ShowReferences && obj.References.Any();
            var fieldsPrefix = hasMore ? "├─" : "└─";
            sb.AppendLine($"{indent}{fieldsPrefix} Fields ({obj.Fields.Count}):");
            
            var fieldsToShow = obj.Fields.Take(options.MaxFields).ToList();
            for (int i = 0; i < fieldsToShow.Count; i++)
            {
                var field = fieldsToShow[i];
                var isLast = i == fieldsToShow.Count - 1 && (!hasMore || obj.Fields.Count > options.MaxFields);
                var fieldPrefix = isLast && !hasMore ? "└─" : "├─";
                sb.AppendLine($"{indent}│  {fieldPrefix} {field.Key}: {FormatFieldValue(field.Value)}");
            }
            
            if (obj.Fields.Count > options.MaxFields)
            {
                var moreFieldsPrefix = hasMore ? "├─" : "└─";
                sb.AppendLine($"{indent}│  {moreFieldsPrefix} ... and {obj.Fields.Count - options.MaxFields} more fields");
            }
        }
        
        // References
        if (options.ShowReferences && obj.References.Any())
        {
            sb.AppendLine($"{indent}└─ References ({obj.References.Count}):");
            
            var referencesToShow = obj.References.Take(options.MaxReferences).ToList();
            for (int i = 0; i < referencesToShow.Count; i++)
            {
                var reference = referencesToShow[i];
                var isLast = i == referencesToShow.Count - 1;
                var refPrefix = isLast ? "└─" : "├─";
                
                var targetObj = snapshot.GetObject(reference.TargetAddress);
                var targetInfo = targetObj != null ? 
                    $"{targetObj.TypeName} ({FormatBytes(targetObj.Size)})" : 
                    "Unknown";
                
                sb.AppendLine($"{indent}   {refPrefix} {reference.FieldName} -> 0x{reference.TargetAddress:X} {targetInfo}");
                
                // Recursively inspect referenced objects
                if (currentDepth < options.MaxDepth && targetObj != null)
                {
                    var recursiveInspection = InspectDetailed(snapshot, targetObj, options, currentDepth + 1, visited);
                    if (!string.IsNullOrEmpty(recursiveInspection))
                    {
                        // Indent the recursive content properly
                        var indentedRecursive = string.Join(Environment.NewLine, 
                            recursiveInspection.Split(Environment.NewLine)
                                .Select(line => string.IsNullOrEmpty(line) ? line : $"{indent}      {line}"));
                        sb.AppendLine(indentedRecursive);
                    }
                }
            }
            
            if (obj.References.Count > options.MaxReferences)
            {
                sb.AppendLine($"{indent}      ... and {obj.References.Count - options.MaxReferences} more references");
            }
        }
        
        return sb.ToString();
    }
    
    private static string InspectJson(HeapSnapshot snapshot, ObjectInfo obj, InspectOptions options)
    {
        var data = new
        {
            Address = $"0x{obj.Address:X}",
            TypeName = obj.TypeName,
            Size = obj.Size,
            RetainedSize = obj.RetainedSize,
            Generation = obj.Generation,
            Fields = options.ShowFields ? obj.Fields.Take(options.MaxFields).ToDictionary(f => f.Key, f => f.Value) : null,
            References = options.ShowReferences ? obj.References.Take(options.MaxReferences).Select(r => new
            {
                FieldName = r.FieldName,
                TargetAddress = $"0x{r.TargetAddress:X}",
                TargetType = r.TypeName
            }).ToList() : null,
            AllocationInfo = options.ShowAllocationInfo ? obj.AllocationInfo : null
        };
        
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }
    
    private static string InspectDebug(HeapSnapshot snapshot, ObjectInfo obj, InspectOptions options)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine($"Object: {obj.TypeName}");
        sb.AppendLine($"Address: 0x{obj.Address:X}");
        sb.AppendLine($"Size: {FormatBytes(obj.Size)}");
        sb.AppendLine($"Retained Size: {FormatBytes(obj.RetainedSize)}");
        sb.AppendLine($"Generation: {obj.Generation}");
        
        // Show GC root information
        if (options.ShowGCRoots)
        {
            if (obj.GCRootPaths.Any())
            {
                sb.AppendLine($"GC Roots ({obj.GCRootPaths.Count}):");
                foreach (var rootPath in obj.GCRootPaths)
                {
                    sb.AppendLine($"  {rootPath.RootKind} @ 0x{rootPath.RootAddress:X} ({rootPath.RootName})");
                    
                    // Try to get more information about the root if possible
                    var rootTypeInfo = GetGCRootTypeInfo(snapshot, rootPath);
                    if (!string.IsNullOrEmpty(rootTypeInfo))
                    {
                        sb.AppendLine($"    Root Type: {rootTypeInfo}");
                    }
                }
            }
            else
            {
                // Check if this object might be a GC root by checking if it has incoming references
                var incomingRefs = GetIncomingReferences(snapshot, obj.Address);
                if (incomingRefs.Count == 0)
                {
                    sb.AppendLine("GC Roots: Potential root (no incoming references found)");
                }
                else
                {
                    sb.AppendLine($"GC Roots: Not a root ({incomingRefs.Count} incoming references)");
                }
            }
        }
        
        if (options.ShowFields && obj.Fields.Any())
        {
            sb.AppendLine($"Fields ({obj.Fields.Count}):");
            foreach (var field in obj.Fields.Take(options.MaxFields))
            {
                sb.AppendLine($"  {field.Key}: {FormatFieldValue(field.Value)}");
            }
            if (obj.Fields.Count > options.MaxFields)
            {
                sb.AppendLine($"  ... and {obj.Fields.Count - options.MaxFields} more fields");
            }
        }
        
        if (options.ShowReferences && obj.References.Any())
        {
            sb.AppendLine($"References ({obj.References.Count}):");
            foreach (var reference in obj.References.Take(options.MaxReferences))
            {
                // Get target object info for more detailed reference information
                var targetObj = snapshot.GetObject(reference.TargetAddress);
                var targetSize = targetObj != null ? $", {FormatBytes(targetObj.Size)}" : "";
                sb.AppendLine($"  {reference.FieldName} -> 0x{reference.TargetAddress:X} ({reference.TypeName}{targetSize})");
            }
            if (obj.References.Count > options.MaxReferences)
            {
                sb.AppendLine($"  ... and {obj.References.Count - options.MaxReferences} more references");
            }
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Attempts to get type information about a GC root.
    /// </summary>
    private static string GetGCRootTypeInfo(HeapSnapshot snapshot, GCRootPath rootPath)
    {
        try
        {
            if (snapshot.Runtime?.Heap == null) return "";
            
            // Try to get the object at the root address to determine its type
            var rootObj = snapshot.Runtime.Heap.GetObject(rootPath.RootAddress);
            if (rootObj.IsValid && rootObj.Type?.Name != null)
            {
                return rootObj.Type.Name;
            }
            
            // For certain root kinds, we can provide more specific information
            return rootPath.RootKind switch
            {
                "StrongHandle" => "Strong GC Handle",
                "WeakHandle" => "Weak GC Handle",
                "Pinned" => "Pinned Object",
                "Stack" => "Stack Variable",
                "Finalizer" => "Finalizer Queue",
                "Static" => "Static Variable",
                "Thread" => "Thread Object",
                "AsyncPinned" => "Async Pinned Object",
                _ => rootPath.RootKind
            };
        }
        catch
        {
            return "";
        }
    }
    
    /// <summary>
    /// Gets incoming references to an object for GC root analysis.
    /// </summary>
    private static List<ObjectReference> GetIncomingReferences(HeapSnapshot snapshot, ulong objectAddress)
    {
        var incomingRefs = new List<ObjectReference>();
        
        try
        {
            // This is a simplified version - in a full implementation we'd maintain
            // a reverse reference index for performance
            foreach (var obj in snapshot.Objects.Values)
            {
                foreach (var reference in obj.References)
                {
                    if (reference.TargetAddress == objectAddress)
                    {
                        incomingRefs.Add(reference);
                    }
                }
                
                // Limit search to prevent performance issues
                if (incomingRefs.Count > 100) break;
            }
        }
        catch
        {
            // Return what we have
        }
        
        return incomingRefs;
    }
    
    private static string FormatFieldValue(object? value, bool compact = false)
    {
        return value switch
        {
            null => "null",
            string s when s.StartsWith("<object:") => s, // Already formatted object reference
            string s when s.StartsWith("<error") => s, // Already formatted error
            string s when s.StartsWith("<primitive:") => s, // Already formatted primitive
            string s when compact && s.Length > 20 => $"\"{s[..17]}...\"",
            string s when !compact && s.Length > 50 => $"\"{s[..47]}...\" (string, {s.Length} chars)",
            string s => $"\"{s}\"",
            byte[] arr => $"byte[{arr.Length}] ({FormatBytes((ulong)arr.Length)})",
            Array arr => $"{arr.GetType().GetElementType()?.Name}[{arr.Length}]",
            bool b => b.ToString().ToLower(),
            char c => $"'{c}'",
            _ when value.GetType().IsPrimitive => $"{value}",
            _ => compact ? value.ToString() : $"{value} ({value.GetType().Name})"
        };
    }
    
    private static string FormatBytes(ulong bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }
}