using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Sherlock.Core.Profiling;

namespace Sherlock.Tests;

/// <summary>Method frames for test fixtures, spelled <c>Namespace.Type.Name&lt;T&gt;(parameters)</c>; the namespace,
/// type, generic parameters, and parameters are optional (no parentheses means <c>()</c>). The same spelling in the
/// same module is the same method.</summary>
internal static class Frames
{
    private static readonly ConcurrentDictionary<(string Module, string Method), uint> Tokens = new();
    private static int _lastToken;

    public static MethodFrame Method(string spelling, string module = "App.dll", string returnType = "void")
    {
        int open = spelling.IndexOf('(');
        string path = open < 0 ? spelling : spelling[..open];
        string parameters = open < 0 ? "()" : spelling[open..];
        int nameAt = path.EndsWith("..ctor", StringComparison.Ordinal) || path.EndsWith("..cctor", StringComparison.Ordinal)
            ? path.LastIndexOf("..", StringComparison.Ordinal)
            : path.LastIndexOf('.');
        string name = path[(nameAt + 1)..];
        string owner = nameAt < 0 ? "" : path[..nameAt];
        int typeAt = owner.LastIndexOf('.');
        int generic = name.IndexOf('<');
        uint token = Tokens.GetOrAdd((module, $"{spelling}:{returnType}"), _ => 0x06000000u | (uint)Interlocked.Increment(ref _lastToken));
        return new MethodFrame(new Guid(MD5.HashData(Encoding.UTF8.GetBytes(module))), module, token,
            typeAt < 0 ? "" : owner[..typeAt], owner[(typeAt + 1)..],
            generic < 0 ? name : name[..generic], generic < 0 ? "" : name[generic..],
            parameters, parameters, returnType, returnType, problem: null);
    }

    /// <summary>A stack, root to leaf, of methods in <c>App.dll</c>.</summary>
    public static MethodFrame[] Stack(params string[] spellings) => spellings.Select(spelling => Method(spelling)).ToArray();
}
