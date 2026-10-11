using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Sherlock.Core.Profiling;

/// <summary>A method in an allocation stack, as one profile recorded it. Its identity is its module's version id
/// and its metadata token: two methods with the same name in different assemblies are different frames, and the
/// same assembly loaded twice is the same frame. A method whose module couldn't be read has no identity to share
/// and equals only itself. Identities are never shown. The name comes in parts as metadata spells them;
/// <see cref="Label"/> is what to display.</summary>
public sealed class MethodFrame : IEquatable<MethodFrame>
{
    public MethodFrame(Guid mvid, string module, uint token, string ns, string type, string name, string genericParameters,
        string parameters, string qualifiedParameters, string returnType, string qualifiedReturnType, string? problem)
    {
        Mvid = mvid;
        Module = module;
        Token = token;
        Namespace = ns;
        Type = type;
        Name = name;
        GenericParameters = genericParameters;
        Parameters = parameters;
        QualifiedParameters = qualifiedParameters;
        ReturnType = returnType;
        QualifiedReturnType = qualifiedReturnType;
        Problem = string.IsNullOrEmpty(problem) ? null : problem;
        Label = Compose(LabelDetail.None);
    }

    public Guid Mvid { get; }
    /// <summary>The module's file name, e.g. <c>System.Private.CoreLib.dll</c>; empty when the profiler couldn't read it.</summary>
    public string Module { get; }
    public uint Token { get; }
    /// <summary><c>System.Collections.Generic</c>; empty for the global namespace.</summary>
    public string Namespace { get; }
    /// <summary>The declaring type, nested types joined by '+': <c>Dictionary&lt;TKey, TValue&gt;+Entry</c>.</summary>
    public string Type { get; }
    /// <summary>The metadata name: <c>Map</c>, <c>.ctor</c>, or <c>System.IDisposable.Dispose</c> for an explicit
    /// interface implementation.</summary>
    public string Name { get; }
    public string GenericParameters { get; }
    /// <summary><c>(int, List&lt;Order&gt;)</c>.</summary>
    public string Parameters { get; }
    /// <summary><c>(int, System.Collections.Generic.List&lt;App.Order&gt;)</c>.</summary>
    public string QualifiedParameters { get; }
    public string ReturnType { get; }
    public string QualifiedReturnType { get; }
    /// <summary>Why the profiler couldn't name the method; null when it could.</summary>
    public string? Problem { get; }

    /// <summary><c>Mapper.Map(int)</c>, with only as much more as it takes to tell the method from every other method
    /// in the same profile (see <see cref="MethodLabels"/>). Control characters are escaped.</summary>
    public string Label { get; internal set; }

    /// <summary>Whether <paramref name="query"/> names this method: <c>Mapper.Map</c> or <c>App.Mapper.Map</c> for every
    /// overload, <c>Mapper.Map(int)</c> or its namespace-qualified spellings for one, or its <see cref="Label"/>. A label
    /// that numbering made unique also has a twin spelled the same without a number; see
    /// <see cref="AllocationProfile.MethodsMatching"/>.</summary>
    public bool Matches(string query)
    {
        if (query.Length == 0)
        {
            return false;
        }
        if (query == Label)
        {
            return true;
        }
        if (Problem is not null)
        {
            return false;
        }
        foreach (string owner in Namespace.Length == 0 ? [Type] : new[] { Type, $"{Namespace}.{Type}" })
        {
            foreach (string method in new[] { $"{owner}.{Name}", $"{owner}.{Name}{GenericParameters}" })
            {
                if (query == method ||
                    query == method + Parameters || query == method + QualifiedParameters ||
                    query == $"{method}{Parameters}:{ReturnType}" || query == $"{method}{QualifiedParameters}:{QualifiedReturnType}")
                {
                    return true;
                }
            }
        }
        return false;
    }

    public override string ToString() => Label;

    public bool Equals(MethodFrame? other) =>
        other is not null && (ReferenceEquals(this, other) || Mvid != Guid.Empty && Mvid == other.Mvid && Token == other.Token);

    public override bool Equals(object? obj) => Equals(obj as MethodFrame);

    public override int GetHashCode() => Mvid == Guid.Empty ? RuntimeHelpers.GetHashCode(this) : HashCode.Combine(Mvid, Token);

    /// <summary><c>Mapper.Map(int)</c> with the given details added: <c>App.Mapper.Map(Api.Order):long [LibA]</c>.</summary>
    internal string Compose(LabelDetail detail)
    {
        string label;
        if (Problem is not null)
        {
            label = "<unresolved method>";
        }
        else
        {
            string owner = detail.HasFlag(LabelDetail.Namespace) && Namespace.Length > 0 ? $"{Namespace}.{Type}" : Type;
            string parameters = detail.HasFlag(LabelDetail.QualifiedParameters) ? QualifiedParameters : Parameters;
            label = $"{owner}.{Name}{GenericParameters}{parameters}";
            if (detail.HasFlag(LabelDetail.QualifiedReturnType))
            {
                label += $":{QualifiedReturnType}";
            }
            else if (detail.HasFlag(LabelDetail.ReturnType))
            {
                label += $":{ReturnType}";
            }
        }
        if (detail.HasFlag(LabelDetail.Module) && Module.Length > 0)
        {
            label += $" [{AssemblyName(Module)}]";
        }
        return Printable.Escape(label);
    }

    // A module's metadata name is usually its file name; some omit the extension, and dots don't mark one.
    private static string AssemblyName(string module) =>
        module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || module.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? module[..^4]
            : module;
}

[Flags]
internal enum LabelDetail
{
    None = 0,
    Namespace = 1,
    QualifiedParameters = 2,
    ReturnType = 4,
    QualifiedReturnType = 8,
    Module = 16,
}

/// <summary>Labels the methods of one profile. Each starts as <c>Type.Method(parameters)</c>. Methods whose labels
/// would be equal get the first detail that tells some of them apart: the namespace, qualified parameters, the return
/// type, its namespace, then the module; this repeats until no detail helps, and any still equal are numbered.
/// Labels never show identities.</summary>
public static class MethodLabels
{
    private static readonly LabelDetail[] Details =
        [LabelDetail.Namespace, LabelDetail.QualifiedParameters, LabelDetail.ReturnType, LabelDetail.QualifiedReturnType, LabelDetail.Module];

    // Each round adds one detail per collision, and real profiles settle within a few. Names crafted so that every
    // detail creates a new collision could otherwise take a round per method.
    private const int MaxRounds = 16;

    public static void Assign(IReadOnlyList<MethodFrame> methods)
    {
        var details = new LabelDetail[methods.Count];
        string[] labels = methods.Select(method => method.Compose(LabelDetail.None)).ToArray();
        for (int round = 0; Collisions(labels) is { Count: > 0 } collisions; round++)
        {
            bool split = false;
            if (round < MaxRounds)
            {
                foreach (int[] group in collisions)
                {
                    split |= Split(methods, group, details, labels);
                }
            }
            if (!split)
            {
                Number(labels, collisions);
            }
        }
        for (int i = 0; i < methods.Count; i++)
        {
            methods[i].Label = labels[i];
        }
    }

    // Adds the first detail that gives a group of equal labels more than one label. A detail all of them share can
    // never help, alone or with others, so when none splits the group, only numbers can.
    private static bool Split(IReadOnlyList<MethodFrame> methods, int[] group, LabelDetail[] details, string[] labels)
    {
        foreach (LabelDetail detail in Details)
        {
            string[] candidates = group.Select(index => methods[index].Compose(details[index] | detail)).ToArray();
            if (candidates.Distinct(StringComparer.Ordinal).Skip(1).Any())
            {
                for (int i = 0; i < group.Length; i++)
                {
                    details[group[i]] |= detail;
                    labels[group[i]] = candidates[i];
                }
                return true;
            }
        }
        return false;
    }

    private static List<int[]> Collisions(string[] labels) =>
        Enumerable.Range(0, labels.Length)
            .GroupBy(index => labels[index], StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.ToArray())
            .ToList();

    // No detail tells these apart: number them after the first, skipping numbers another label already uses.
    private static void Number(string[] labels, List<int[]> collisions)
    {
        var taken = new HashSet<string>(labels, StringComparer.Ordinal);
        foreach (int[] group in collisions)
        {
            int next = 2;
            foreach (int index in group.Skip(1))
            {
                string numbered;
                do
                {
                    numbered = $"{labels[group[0]]} #{next++}";
                }
                while (!taken.Add(numbered));
                labels[index] = numbered;
            }
        }
    }
}
