using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Diagnostics.NETCore.Client;

namespace Sherlock.Core.Collection;

public sealed record DotnetProcess(int Pid, string Name);

public static class ProcessLocator
{
    public static IReadOnlyList<DotnetProcess> List() =>
        DiagnosticsClient.GetPublishedProcesses()
            .Select(pid => new DotnetProcess(pid, NameOf(pid)))
            .OrderBy(p => p.Pid)
            .ToList();

    public static IReadOnlyList<DotnetProcess> FindByName(string name) => List()
            .Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

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
