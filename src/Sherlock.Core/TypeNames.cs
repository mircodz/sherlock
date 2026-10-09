using System.Text;

namespace Sherlock.Core;

/// <summary>Formatting helpers for managed type names in command output.</summary>
public static class TypeNames
{
    /// <summary>
    /// The type's short name: namespaces stripped everywhere, including generic arguments and array element types
    /// (<c>System.Collections.Generic.Dictionary&lt;System.String, App.Order[]&gt;+Entry[]</c> becomes
    /// <c>Dictionary&lt;String, Order[]&gt;+Entry[]</c>). Compiler-generated names such as <c>&lt;&gt;c</c> are kept.
    /// </summary>
    public static string Short(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return typeName;
        }
        var result = new StringBuilder(typeName.Length);
        int index = 0;
        AppendShort(typeName, ref index, result, stopAtArgumentEnd: false);
        return result.ToString();
    }

    /// <summary>The namespace of the outermost type (<c>System.Collections.Generic</c>), or empty when there is none.</summary>
    public static string Namespace(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return "";
        }
        int end = 0;
        while (end < typeName.Length && typeName[end] is not ('<' or '+' or '[' or ','))
        {
            end++;
        }
        // A compiler-generated name ("<>c") starts with '<' right after a dot; it is part of the type, not generic.
        int dot = typeName.LastIndexOf('.', end == 0 ? 0 : end - 1);
        return dot <= 0 ? "" : typeName[..dot];
    }

    private static void AppendShort(string name, ref int index, StringBuilder result, bool stopAtArgumentEnd)
    {
        int segment = result.Length; // where the current dotted name starts; a '.' drops everything since
        while (index < name.Length)
        {
            char c = name[index];
            if (stopAtArgumentEnd && c is ',' or '>')
            {
                return;
            }
            switch (c)
            {
                case '.':
                    result.Length = segment;
                    index++;
                    break;
                case '+':
                    result.Append(c);
                    segment = result.Length;
                    index++;
                    break;
                case '<' when IsGenericOpen(name, index):
                    result.Append('<');
                    index++;
                    while (index < name.Length)
                    {
                        while (index < name.Length && name[index] == ' ')
                        {
                            index++;
                        }
                        AppendShort(name, ref index, result, stopAtArgumentEnd: true);
                        if (index >= name.Length || name[index] == '>')
                        {
                            break;
                        }
                        result.Append(", ");
                        index++; // ','
                    }
                    result.Append('>');
                    index++;
                    break;
                case '<':
                    // Compiler-generated identifier such as "<>c" or "<Main>d__0": copy through its closing '>'.
                    int close = name.IndexOf('>', index);
                    int end = close < 0 ? name.Length : close + 1;
                    result.Append(name, index, end - index);
                    index = end;
                    break;
                case '[':
                    int bracket = name.IndexOf(']', index);
                    int stop = bracket < 0 ? name.Length : bracket + 1;
                    result.Append(name, index, stop - index);
                    index = stop;
                    break;
                default:
                    result.Append(c);
                    index++;
                    break;
            }
        }
    }

    // Generic argument lists follow an identifier; compiler-generated names start a segment with '<'.
    private static bool IsGenericOpen(string name, int index) =>
        index > 0 && name[index - 1] is not ('.' or '+' or '<' or ' ' or ',');
}
