using System;

namespace Sherlock.Core.Profiling;

/// <summary>Parses profiler frame labels: <c>Ns.Type&lt;T&gt;.Method&lt;U&gt;(params)</c>, optionally followed by
/// <c>:ReturnType</c> or a <c>[token]</c> suffix when needed to tell same-named methods apart. Parameter lists may
/// contain qualified type names, so dots are only meaningful before the parameter list.</summary>
public static class FrameNames
{
    /// <summary>Splits a label into its declaring type and its method part, e.g.
    /// <c>("Ns.Type", "Method(Api.Order)")</c> or <c>("Ns.Type", ".ctor(int)")</c>.</summary>
    public static (string Type, string Method) Split(string frame)
    {
        int dot = PreviousDot(frame, ParametersStart(frame));
        if (dot <= 0)
        {
            return ("", frame);
        }
        // Constructors: "Type..ctor(...)" splits before ".ctor".
        int split = frame[dot - 1] == '.' ? dot - 1 : dot;
        return (frame[..split], frame[(split + 1)..]);
    }

    /// <summary>The label without its namespace: <c>Type.Method(params)</c>.</summary>
    public static string ShortMethod(string frame)
    {
        (string type, string method) = Split(frame);
        int dot = PreviousDot(type, type.Length);
        return type.Length == 0 ? method : $"{type[(dot + 1)..]}.{method}";
    }

    /// <summary>Whether <paramref name="frame"/> is <paramref name="method"/>, or one of its overloads when the
    /// parameter list is omitted, or one of its return-type variants when only the return type is omitted.</summary>
    public static bool Matches(string frame, string method)
    {
        if (method.Length == 0)
        {
            return frame.Length == 0;
        }
        if (!frame.StartsWith(method, StringComparison.Ordinal))
        {
            return false;
        }
        if (frame.Length == method.Length)
        {
            return true;
        }
        ReadOnlySpan<char> rest = frame.AsSpan(method.Length);
        if (method.EndsWith(')'))
        {
            return rest[0] == ':' || rest.StartsWith(" [", StringComparison.Ordinal);
        }
        if (rest[0] == '<')
        {
            // A generic method's type parameters, not the declaring type's: "Method<T>(".
            int close = Close(rest, 0, '<', '>');
            return close > 0 && close + 1 < rest.Length && rest[close + 1] == '(';
        }
        return rest[0] == '(';
    }

    private static int ParametersStart(string frame)
    {
        int depth = 0;
        for (int i = 0; i < frame.Length; i++)
        {
            switch (frame[i])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case '(' when depth == 0: return i;
            }
        }
        return frame.Length;
    }

    private static int PreviousDot(string value, int before)
    {
        int depth = 0;
        for (int i = before - 1; i >= 0; i--)
        {
            switch (value[i])
            {
                case '>': depth++; break;
                case '<': depth--; break;
                case '.' when depth == 0: return i;
            }
        }
        return -1;
    }

    private static int Close(ReadOnlySpan<char> text, int open, char opening, char closing)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == opening)
            {
                depth++;
            }
            else if (text[i] == closing && --depth == 0)
            {
                return i;
            }
        }
        return -1;
    }
}
