using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;

namespace Sherlock.Core.Collection;

public sealed record RunProcess(int Pid, string Name, bool IsRoot, bool IsDotnet, int ParentPid = 0);

public sealed record HeapStats(long Total, long Gen0, long Gen1, long Gen2, long Loh, long Poh);

public sealed record RunOptions
{
    public required IReadOnlyList<string> Command { get; init; }
    public bool Profile { get; init; }
    public bool Correlate { get; init; }
    public bool CollectChildren { get; init; }
    public string? SnapshotOn { get; init; }
    public string? OutputDirectory { get; init; }
    public string? ProfilerPath { get; init; }
    public bool NeedsProfiler => Profile || Correlate || CollectChildren || SnapshotOn is not null || ProfilerPath is not null;
}

/// <summary>A launched process tree and its profiler connection.</summary>
public sealed class RunTarget : IDisposable
{
    private Process? _root;
    private readonly HashSet<string> _seenAllocations = [];

    private string? _captureDir;
    private string? _allocationTemplate;
    private bool _collectChildren;

    public string? AllocationPath { get; private set; }

    private bool _correlate;

    private ProfilerControl? _control;

    private readonly ConcurrentQueue<(int Pid, string Name)> _triggerHits = new();

    private readonly ConcurrentDictionary<int, string> _names = new();

    public string? NameFor(int pid) => _names.TryGetValue(pid, out string? n) ? n : null;

    public bool HasCorrelation => _correlate;

    public IReadOnlyList<string> Features => _control?.Features ?? [];

    public RunOptions Options { get; private set; } = null!;

    public bool HasExited => _root?.HasExited ?? false;

    public int? ExitCode => _root is { HasExited: true } process ? process.ExitCode : null;

    public int Pid => _root?.Id ?? 0;

    public string Name { get; private set; } = "";

    /// <summary>The single app child when present, otherwise the first live .NET process.</summary>
    public int PrimaryPid
    {
        get
        {
            List<RunProcess> dotnet = Processes().Where(p => p.IsDotnet).ToList();
            List<RunProcess> children = dotnet.Where(p => !p.IsRoot).ToList();
            if (children.Count == 1)
            {
                return children[0].Pid;
            }
            return dotnet.Count > 0 ? dotnet[0].Pid : Pid;
        }
    }

    public string? LogPath { get; private set; }

    private StreamWriter? _log;
    private readonly Lock _logLock = new();

    private RunTarget() { }

    public static RunTarget Start(RunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Command is null || options.Command.Count == 0)
        {
            throw new ArgumentException("Run command cannot be empty.", nameof(options));
        }
        options = options with { Command = options.Command.ToArray() };

        var target = new RunTarget();
        try
        {
            target.StartCore(options);
            return target;
        }
        catch
        {
            target.Kill();
            target.Dispose();
            throw;
        }
    }

    private void StartCore(RunOptions options)
    {
        string? profilerPath = options.NeedsProfiler ? FindProfiler(options.ProfilerPath) : null;
        if (options.NeedsProfiler && profilerPath is null)
        {
            throw new DumpAnalysisException($"Profiler library ({ProfilerFileName}) not found. Build it with src/native/build.sh or set SHERLOCK_PROFILER_PATH.");
        }

        _captureDir = options.OutputDirectory is null ? null : Path.GetFullPath(options.OutputDirectory);
        if (profilerPath is not null && _captureDir is null)
        {
            _captureDir = Directory.CreateTempSubdirectory("sherlock-profile-").FullName;
        }
        if (_captureDir is not null)
        {
            Directory.CreateDirectory(_captureDir);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_captureDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        _collectChildren = options.CollectChildren;
        _correlate = options.Correlate;
        Options = options with { OutputDirectory = _captureDir, ProfilerPath = profilerPath };
        var psi = StartInfo(options.Command);

        if (profilerPath is not null)
        {
            ConfigureProfiler(psi, profilerPath, options);
        }

        _root = Process.Start(psi) ?? throw new DumpAnalysisException($"Failed to start process: {options.Command[0]}");
        if (_allocationTemplate is not null)
        {
            AllocationPath = InsertPid(_allocationTemplate, _root.Id);
        }
        Name = Path.GetFileName(options.Command[0]);
        _names[_root.Id] = Name;
        StartLog();
    }

    private static ProcessStartInfo StartInfo(IReadOnlyList<string> command)
    {
        var psi = new ProcessStartInfo(command[0]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in command.Skip(1))
        {
            psi.ArgumentList.Add(arg);
        }
        return psi;
    }

    private void StartLog()
    {
        LogPath = _captureDir is not null ? Path.Combine(_captureDir, "run.log") : Path.Combine(Path.GetTempPath(), $"sherlock-run-{_root!.Id}.log");
        _log = new StreamWriter(LogPath, append: false) { AutoFlush = true };
        _root!.OutputDataReceived += (_, e) => WriteLog(e.Data);
        _root.ErrorDataReceived += (_, e) => WriteLog(e.Data);
        _root.BeginOutputReadLine();
        _root.BeginErrorReadLine();
    }

    private void ConfigureProfiler(ProcessStartInfo psi, string profilerPath, RunOptions options)
    {
        psi.Environment["CORECLR_ENABLE_PROFILING"] = "1";
        psi.Environment["CORECLR_PROFILER"] = "{cf0d821e-299b-5307-a3d8-b283c03916dd}";
        psi.Environment["CORECLR_PROFILER_PATH"] = profilerPath;
        _allocationTemplate = Path.Combine(_captureDir!, "allocations.slab");
        psi.Environment["SHERLOCK_PROFILE_OUT"] = _allocationTemplate;

        if (!string.IsNullOrWhiteSpace(options.SnapshotOn))
        {
            psi.Environment["SHERLOCK_SNAPSHOT_ON"] = options.SnapshotOn;
        }
        if (options.Correlate)
        {
            psi.Environment["SHERLOCK_CORRELATE"] = "1";
            psi.Environment["SHERLOCK_CORRELATE_OUT"] = Path.Combine(_captureDir!, "provenance.slab");
        }

        _control = new ProfilerControl(ControlSocketPath(_captureDir));
        _control.EventReceived += (pid, fields) =>
        {
            if (fields.Length >= 3 && fields[1] == ProfilerControl.SnapshotTrigger)
            {
                _triggerHits.Enqueue((pid, fields[2]));
            }
        };
        psi.Environment["SHERLOCK_CONTROL_SOCKET"] = _control.SocketPath;
    }

    private void WriteLog(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_logLock)
        {
            _log?.WriteLine(line);
        }
    }

    /// <summary>Returns the last <paramref name="tail"/> lines of captured output.</summary>
    public IReadOnlyList<string> ReadLog(int tail)
    {
        if (LogPath is null || !File.Exists(LogPath))
        {
            return [];
        }

        try
        {
            string[] lines = File.ReadAllLines(LogPath);
            return tail >= lines.Length ? lines : lines[^tail..];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Returns newly available exit-time allocation profiles.</summary>
    public IReadOnlyList<(int Pid, string Path)> PollAllocationProfiles()
    {
        if (AllocationPath is null || _root is null || !_root.HasExited)
        {
            return [];
        }

        if (!_collectChildren)
        {
            return File.Exists(AllocationPath) && _seenAllocations.Add(AllocationPath) ? [(Pid, AllocationPath)] : [];
        }

        string stem = Path.GetFileNameWithoutExtension(_allocationTemplate!);
        string ext = Path.GetExtension(_allocationTemplate!);
        var found = new List<(int, string)>();
        foreach (string file in Directory.EnumerateFiles(_captureDir!, $"{stem}.*{ext}"))
        {
            string inner = Path.GetFileNameWithoutExtension(file); // "allocations.<pid>"
            int dot = inner.LastIndexOf('.');
            if (dot >= 0 && int.TryParse(inner[(dot + 1)..], out int pid) && _seenAllocations.Add(file))
            {
                found.Add((pid, file));
            }
        }
        return found;
    }

    internal static string InsertPid(string path, int pid)
    {
        string dir = Path.GetDirectoryName(path) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(path);
        return Path.Combine(dir, $"{stem}.{pid}{Path.GetExtension(path)}");
    }

    private static string ControlSocketPath(string? preferredDirectory)
    {
        string name = $"sl-{Guid.NewGuid():N}.sock";
        string preferred = Path.Combine(preferredDirectory ?? Path.GetTempPath(), name);
        if (Encoding.UTF8.GetByteCount(preferred) <= 100)
        {
            return preferred;
        }

        string fallback = Path.Combine(Path.GetTempPath(), name);
        if (Encoding.UTF8.GetByteCount(fallback) <= 100)
        {
            return fallback;
        }

        if (!OperatingSystem.IsWindows() && Directory.Exists("/tmp"))
        {
            return Path.Combine("/tmp", name);
        }

        throw new DumpAnalysisException("The profiler control socket path exceeds the platform limit.");
    }

    public IReadOnlyList<(int Pid, string Name)> PollTriggers()
    {
        if (_root is null)
        {
            return [];
        }

        List<(int, string)>? hits = null;
        while (_triggerHits.TryDequeue(out (int Pid, string Name) hit))
        {
            (hits ??= []).Add(hit);
        }
        return (IReadOnlyList<(int, string)>?)hits ?? [];
    }

    /// <summary>Forces a GC and captures cumulative allocations plus live-object correlation.</summary>
    public (string Path, long GcAtEmit) CaptureCorrelation(int pid, TimeSpan timeout)
    {
        if (_control is null || !IsAlive(pid))
        {
            throw new DumpAnalysisException($"Process {pid} has no live profiler control channel.");
        }

        (bool ok, string[] fields) = Request(pid, ProfilerControl.EmitCorrelation, timeout);
        if (!ok)
        {
            throw new DumpAnalysisException(fields.FirstOrDefault() ?? $"Profiler in process {pid} did not produce correlation data.");
        }

        // The profiler reports its own (per-pid) sidecar path in the response.
        string path = fields.Length > 0 ? fields[0] : "";
        long gc = fields.Length > 1 && long.TryParse(fields[1], out long g) ? g : -1;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new DumpAnalysisException($"Profiler in process {pid} reported a missing correlation file.");
        }
        return (path, gc);
    }

    public long GcCount(int pid, TimeSpan timeout)
    {
        if (_control is null || !IsAlive(pid))
        {
            return -1;
        }

        (bool ok, string[] fields) = Request(pid, ProfilerControl.GcCount, timeout);
        return ok && fields.Length > 0 && long.TryParse(fields[0], out long g) ? g : -1;
    }

    public HeapStats? HeapSize(int pid, TimeSpan timeout)
    {
        if (_control is null || !IsAlive(pid))
        {
            return null;
        }

        (bool ok, string[] fields) = Request(pid, ProfilerControl.HeapSize, timeout);
        if (!ok || fields.Length < 6)
        {
            return null;
        }

        // total \t gen0 \t gen1 \t gen2 \t loh \t poh (bytes)
        var v = new long[6];
        for (int i = 0; i < 6; i++)
        {
            if (!long.TryParse(fields[i], out v[i]))
            {
                return null;
            }
        }
        return new HeapStats(v[0], v[1], v[2], v[3], v[4], v[5]);
    }

    public (bool Ok, string Detail) ArmTrigger(int pid, string spec, TimeSpan timeout)
    {
        if (_control is null || !IsAlive(pid))
        {
            return (false, "no live profiler");
        }
        (bool ok, string[] fields) = Request(pid, ProfilerControl.ArmTrigger, timeout, spec);
        return (ok, fields.Length > 0 ? fields[0] : (ok ? "armed" : "failed"));
    }

    /// <summary>Captures the current cumulative allocation profile.</summary>
    public string CaptureAllocations(int pid, TimeSpan timeout)
    {
        if (_control is null || !IsAlive(pid))
        {
            throw new DumpAnalysisException($"Process {pid} has no live profiler control channel.");
        }

        (bool ok, string[] fields) = Request(pid, ProfilerControl.FlushAllocations, timeout);
        string path = fields.Length > 0 ? fields[0] : "";
        if (ok && File.Exists(path))
        {
            return path;
        }
        throw new DumpAnalysisException(ok ? $"Profiler in process {pid} reported a missing allocation file." : fields.FirstOrDefault() ?? $"Profiler in process {pid} did not produce allocation data.");
    }

    /// <summary>Returns the live process tree, root first.</summary>
    public IReadOnlyList<RunProcess> Processes()
    {
        if (_root is null)
        {
            return [];
        }

        HashSet<int> dotnet = DotnetPids();
        Dictionary<int, List<int>> children = ChildrenByParent();
        var result = new List<RunProcess>();

        // BFS from the root, carrying each pid's parent (0 for the root).
        var seen = new HashSet<int> { _root.Id };
        var queue = new Queue<(int Pid, int Parent)>();
        queue.Enqueue((_root.Id, 0));
        while (queue.Count > 0)
        {
            (int pid, int parent) = queue.Dequeue();
            if (IsAlive(pid))
            {
                RunProcess described = Describe(pid, pid == _root.Id, dotnet) with { ParentPid = parent };
                _names[pid] = described.Name;
                result.Add(described);
            }
            if (children.TryGetValue(pid, out List<int>? kids))
            {
                foreach (int child in kids)
                {
                    if (seen.Add(child))
                    {
                        queue.Enqueue((child, pid));
                    }
                }
            }
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

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        await _root!.WaitForExitAsync(cancellationToken);
        return _root.ExitCode;
    }

    private static RunProcess Describe(int pid, bool isRoot, HashSet<int> dotnet) =>
        new(pid, NameOf(pid), isRoot, dotnet.Contains(pid));

    private static HashSet<int> DotnetPids()
    {
        try { return DiagnosticsClient.GetPublishedProcesses().ToHashSet(); }
        catch { return []; }
    }

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
            // No `ps` (e.g. Windows): the tree collapses to just the root.
        }
        return map;
    }

    private (bool Ok, string[] Fields) Request(int pid, string cmd, TimeSpan timeout, params string[] args) =>
        _control!.RequestAsync(pid, cmd, timeout, args).GetAwaiter().GetResult();

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

    private static string ProfilerFileName => OperatingSystem.IsWindows() ? "SherlockProfiler.dll" : OperatingSystem.IsMacOS() ? "libSherlockProfiler.dylib" : "libSherlockProfiler.so";

    private static string? FindProfiler(string? configured)
    {
        configured ??= Environment.GetEnvironmentVariable("SHERLOCK_PROFILER_PATH");
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        string bundled = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeId(), "native", ProfilerFileName);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        return null;
    }

    private static string RuntimeId()
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };
        return $"{os}-{arch}";
    }

    public void Dispose()
    {
        // We don't kill the tree on dispose; the user controls lifetime via `kill`.
        lock (_logLock)
        {
            _log?.Dispose();
            _log = null;
        }
        _control?.Dispose();
        _root?.Dispose();
    }
}
