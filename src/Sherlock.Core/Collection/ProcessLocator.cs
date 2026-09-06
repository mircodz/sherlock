using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.NETCore.Client;

namespace Sherlock.Core.Collection;

public sealed record DotnetProcess(int Pid, string Name);
public sealed record RunProcess(int Pid, string Name, bool IsRoot, bool IsDotnet, int ParentPid = 0);

public static partial class ProcessLocator
{
    public static IReadOnlyList<DotnetProcess> List() =>
        DiagnosticsClient.GetPublishedProcesses()
            .Select(pid => new DotnetProcess(pid, NameOf(pid) ?? "<unknown>"))
            .OrderBy(p => p.Pid)
            .ToList();

    public static IReadOnlyList<DotnetProcess> FindByName(string name) =>
        List()
            .Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>Returns the live descendants and root, with the root first.</summary>
    public static IReadOnlyList<RunProcess> Tree(int rootPid)
    {
        HashSet<int> dotnet = DotnetPids();
        Dictionary<int, List<int>> children = ChildrenByParent();
        return Descendants(rootPid, children)
            .Where(process => IsAlive(process.Pid))
            .Select(process => new RunProcess(process.Pid, NameOf(process.Pid) ?? "<exited>",
                process.Pid == rootPid, dotnet.Contains(process.Pid), process.ParentPid))
            .OrderByDescending(process => process.IsRoot)
            .ThenBy(process => process.Pid)
            .ToList();
    }

    internal static IEnumerable<(int Pid, int ParentPid)> Descendants(
        int rootPid, IReadOnlyDictionary<int, List<int>> children)
    {
        var seen = new HashSet<int> { rootPid };
        var queue = new Queue<(int Pid, int ParentPid)>();
        queue.Enqueue((rootPid, 0));
        while (queue.Count > 0)
        {
            (int pid, int parent) = queue.Dequeue();
            yield return (pid, parent);
            if (!children.TryGetValue(pid, out List<int>? kids))
            {
                continue;
            }
            foreach (int child in kids)
            {
                if (seen.Add(child))
                {
                    queue.Enqueue((child, pid));
                }
            }
        }
    }

    private static HashSet<int> DotnetPids()
    {
        try { return DiagnosticsClient.GetPublishedProcesses().ToHashSet(); }
        catch { return []; }
    }

    private static Dictionary<int, List<int>> ChildrenByParent()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsChildrenByParent();
        }

        var map = new Dictionary<int, List<int>>();
        try
        {
            var psi = new ProcessStartInfo("ps", "-axo pid=,ppid=")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using Process? ps = Process.Start(psi);
            if (ps is null)
            {
                return map;
            }

            string output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(2000);

            foreach (string line in output.Split('\n'))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && int.TryParse(parts[0], out int pid) && int.TryParse(parts[1], out int ppid))
                {
                    (map.TryGetValue(ppid, out List<int>? kids) ? kids : map[ppid] = []).Add(pid);
                }
            }
        }
        catch
        {
            // Process discovery is advisory; the launched root remains usable.
        }
        return map;
    }

    private static Dictionary<int, List<int>> WindowsChildrenByParent()
    {
        var map = new Dictionary<int, List<int>>();
        nint snapshot = WindowsProcessSnapshot.CreateToolhelp32Snapshot(WindowsProcessSnapshot.Process, 0);
        if (snapshot == -1)
        {
            return map;
        }

        try
        {
            var entry = new WindowsProcessSnapshot.ProcessEntry { Size = (uint)Marshal.SizeOf<WindowsProcessSnapshot.ProcessEntry>() };
            if (!WindowsProcessSnapshot.Process32First(snapshot, ref entry))
            {
                return map;
            }

            do
            {
                if (entry.ProcessId > int.MaxValue || entry.ParentProcessId > int.MaxValue)
                {
                    continue;
                }
                int pid = (int)entry.ProcessId;
                int parent = (int)entry.ParentProcessId;
                (map.TryGetValue(parent, out List<int>? children) ? children : map[parent] = []).Add(pid);
                entry.Size = (uint)Marshal.SizeOf<WindowsProcessSnapshot.ProcessEntry>();
            }
            while (WindowsProcessSnapshot.Process32Next(snapshot, ref entry));
        }
        finally
        {
            WindowsProcessSnapshot.CloseHandle(snapshot);
        }
        return map;
    }

    private static partial class WindowsProcessSnapshot
    {
        public const uint Process = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public unsafe struct ProcessEntry
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public nuint DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int BasePriority;
            public uint Flags;
            public fixed char ExeFile[260];
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

        [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool Process32First(nint snapshot, ref ProcessEntry entry);

        [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool Process32Next(nint snapshot, ref ProcessEntry entry);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(nint handle);
    }

    internal static bool IsAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public static string? NameOf(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}
