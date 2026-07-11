namespace Sherlock.CLI.Rendering;

/// <summary>Formatting helpers for managed type names in command output.</summary>
public static class TypeNames
{
    /// <summary>
    /// The type's short name - namespace stripped, generic arguments kept
    /// (e.g. <c>System.Collections.Generic.List&lt;Foo.Bar&gt;</c> -> <c>List&lt;Foo.Bar&gt;</c>).
    /// </summary>
    public static string Short(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return typeName;
        }

        int generic = typeName.IndexOf('<');
        string head = generic < 0 ? typeName : typeName[..generic];
        int dot = head.LastIndexOf('.');
        return dot < 0 ? typeName : typeName[(dot + 1)..];
    }
}
