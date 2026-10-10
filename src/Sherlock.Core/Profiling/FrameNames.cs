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
    public static string ShortMethod(string frame) => frame[ShortStart(frame)..];

    /// <summary>Where <see cref="ShortMethod"/> starts: after the declaring type's namespace, or 0 without one.</summary>
    private static int ShortStart(string frame)
    {
        int dot = PreviousDot(frame, ParametersStart(frame));
        if (dot <= 0)
        {
            return 0;
        }
        int split = frame[dot - 1] == '.' ? dot - 1 : dot;
        return PreviousDot(frame, split) + 1;
    }

    /// <summary>Whether <paramref name="frame"/> is <paramref name="method"/>, or one of its overloads when the
    /// parameter list is omitted, or one of its return-type variants when only the return type is omitted. The query
    /// may also leave out the namespace, as <see cref="ShortMethod"/> displays it.</summary>
    public static bool Matches(string frame, string method)
    {
        if (method.Length == 0)
        {
            return frame.Length == 0;
        }
        return MatchesFrom(frame, 0, method) || ShortStart(frame) is > 0 and int start && MatchesFrom(frame, start, method);
    }

    private static bool MatchesFrom(string frame, int start, string method)
    {
        ReadOnlySpan<char> label = frame.AsSpan(start);
        if (!label.StartsWith(method, StringComparison.Ordinal))
        {
            return false;
        }
        if (label.Length == method.Length)
        {
            return true;
        }
        ReadOnlySpan<char> rest = label[method.Length..];
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
