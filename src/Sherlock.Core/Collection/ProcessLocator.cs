using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

namespace Sherlock.Core.Collection;

/// <summary>A live .NET process that exposes a diagnostics endpoint (can be dumped).</summary>
public sealed record DotnetProcess(int Pid, string Name);

/// <summary>
/// Discovers running .NET processes via the diagnostics IPC channel — every
/// .NET 5+ process publishes one, so this needs no profiler or debugger.
/// </summary>
public static class ProcessLocator
{
    /// <summary>All dumpable .NET processes visible to the current user.</summary>
    public static IReadOnlyList<DotnetProcess> List()
    {
        var result = new List<DotnetProcess>();
        foreach (int pid in DiagnosticsClient.GetPublishedProcesses())
            result.Add(new DotnetProcess(pid, NameOf(pid)));

        return result.OrderBy(p => p.Pid).ToList();
    }

    /// <summary>Processes whose name contains <paramref name="name"/> (case-insensitive).</summary>
    public static IReadOnlyList<DotnetProcess> FindByName(string name) =>
        List().Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();

    private static string NameOf(int pid)
    {
        try
        {
            return Process.GetProcessById(pid).ProcessName;
        }
        catch
        {
            return "<unknown>";
        }
    }
}
