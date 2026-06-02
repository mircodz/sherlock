using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

namespace Sherlock.Core.Collection;

/// <summary>A process in the supervised subtree.</summary>
/// <param name="IsDotnet">Whether it exposes a diagnostics endpoint (and so can be dumped).</param>
public sealed record SupervisedProcess(int Pid, string Name, bool IsRoot, bool IsDotnet);

/// <summary>
/// Launches a target process and tracks its descendant process tree.
///
/// Child discovery walks the OS process tree (via <c>ps</c>) from the root pid,
/// then marks which descendants are .NET — and therefore dumpable — using the
/// public <see cref="DiagnosticsClient.GetPublishedProcesses"/>. (The diagnostics
/// reverse-port channel would be cleaner, but its server type is internal to the
/// client package.) Crash dumps are enabled via inherited runtime env vars.
/// </summary>
public sealed class ProcessSupervisor : IDisposable
{
    private Process? _root;
    private bool _dumpOnCrash;
    private string? _crashDumpPath;
    private bool _crashHarvested;

    /// <summary>True once the launched root process has exited.</summary>
    public bool RootExited => _root?.HasExited ?? false;

    public int? RootExitCode => _root is { HasExited: true } p ? p.ExitCode : null;

    public int RootPid => _root?.Id ?? 0;

    public string? RootName { get; private set; }

    /// <summary>Path to the captured stdout/stderr log, once started.</summary>
    public string? LogPath { get; private set; }

    private StreamWriter? _log;
    private readonly object _logLock = new();

    /// <summary>Launches the target process, capturing its stdout/stderr to a log file.</summary>
    public SupervisedProcess Start(string path, IReadOnlyList<string> args, bool dumpOnCrash)
    {
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        _dumpOnCrash = dumpOnCrash;
        if (dumpOnCrash)
        {
            // Inherited by the whole subtree → any .NET process that crashes self-dumps.
            psi.Environment["DOTNET_DbgEnableMiniDump"] = "1";
            psi.Environment["DOTNET_DbgMiniDumpType"] = "2"; // 2 = heap
            psi.Environment["DOTNET_DbgMiniDumpName"] =
                Path.Combine(Path.GetTempPath(), "sherlock-crash-%p.dmp");
        }

        _root = Process.Start(psi)
            ?? throw new DumpAnalysisException($"Failed to start process: {path}");

        RootName = Path.GetFileName(path);
        // %p in the dump name expands to the pid, so we know the root's crash file path.
        _crashDumpPath = Path.Combine(Path.GetTempPath(), $"sherlock-crash-{_root.Id}.dmp");

        // Capture output to a log so it doesn't trample the REPL prompt.
        LogPath = Path.Combine(Path.GetTempPath(), $"sherlock-run-{_root.Id}.log");
        _log = new StreamWriter(LogPath, append: false) { AutoFlush = true };
        _root.OutputDataReceived += (_, e) => WriteLog(e.Data);
        _root.ErrorDataReceived += (_, e) => WriteLog(e.Data);
        _root.BeginOutputReadLine();
        _root.BeginErrorReadLine();

        return Describe(_root.Id, isRoot: true, DotnetPids());
    }

    private void WriteLog(string? line)
    {
        if (line is null)
            return;
        lock (_logLock)
            _log?.WriteLine(line);
    }

    /// <summary>Returns the last <paramref name="tail"/> lines of captured output.</summary>
    public IReadOnlyList<string> ReadLog(int tail)
    {
        if (LogPath is null || !File.Exists(LogPath))
            return Array.Empty<string>();
        try
        {
            string[] lines = File.ReadAllLines(LogPath);
            return tail >= lines.Length ? lines : lines[^tail..];
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// If the root process has exited and left a crash dump (and we haven't already
    /// taken it), returns its path once. Otherwise null.
    /// </summary>
    public string? TryHarvestRootCrashDump()
    {
        if (!_dumpOnCrash || _root is null || !_root.HasExited || _crashHarvested)
            return null;

        _crashHarvested = true; // check exactly once after exit
        return _crashDumpPath is not null && File.Exists(_crashDumpPath) ? _crashDumpPath : null;
    }

    /// <summary>The live supervised subtree (root + descendants), root first.</summary>
    public IReadOnlyList<SupervisedProcess> List()
    {
        if (_root is null)
            return Array.Empty<SupervisedProcess>();

        HashSet<int> dotnet = DotnetPids();
        var result = new List<SupervisedProcess>();

        foreach (int pid in Descendants(_root.Id))
        {
            if (IsAlive(pid))
                result.Add(Describe(pid, pid == _root.Id, dotnet));
        }

        return result
            .OrderByDescending(p => p.IsRoot)
            .ThenBy(p => p.Pid)
            .ToList();
    }

    public void Kill()
    {
        if (_root is { HasExited: false })
        {
            try { _root.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
    }

    private static SupervisedProcess Describe(int pid, bool isRoot, HashSet<int> dotnet) =>
        new(pid, NameOf(pid), isRoot, dotnet.Contains(pid));

    private static HashSet<int> DotnetPids()
    {
        try { return DiagnosticsClient.GetPublishedProcesses().ToHashSet(); }
        catch { return new HashSet<int>(); }
    }

    /// <summary>The root pid plus every transitive child, from the OS process table.</summary>
    private static IEnumerable<int> Descendants(int root)
    {
        Dictionary<int, List<int>> children = ChildrenByParent();

        var seen = new HashSet<int> { root };
        var queue = new Queue<int>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            int pid = queue.Dequeue();
            yield return pid;
            if (!children.TryGetValue(pid, out List<int>? kids))
                continue;
            foreach (int child in kids)
                if (seen.Add(child))
                    queue.Enqueue(child);
        }
    }

    /// <summary>Parses <c>ps</c> into a parent → children map. Empty on unsupported platforms.</summary>
    private static Dictionary<int, List<int>> ChildrenByParent()
    {
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
                return map;

            string output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(2000);

            foreach (string line in output.Split('\n'))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && int.TryParse(parts[0], out int pid) && int.TryParse(parts[1], out int ppid))
                    (map.TryGetValue(ppid, out List<int>? kids) ? kids : map[ppid] = new List<int>()).Add(pid);
            }
        }
        catch
        {
            // No `ps` (e.g. Windows) — descendants will just be the root for now.
        }
        return map;
    }

    private static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }

    private static string NameOf(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return "<exited>"; }
    }

    public void Dispose()
    {
        // We do not kill the tree on dispose; the user controls lifetime via `kill`.
        lock (_logLock)
        {
            _log?.Dispose();
            _log = null;
        }
        _root?.Dispose();
    }
}
