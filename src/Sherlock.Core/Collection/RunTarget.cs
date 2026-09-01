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
public sealed record CoherentCaptureResult(string DumpPath, string ProvenancePath, long GcCount);
public sealed record RunTrigger(int Pid, string Name, string? ExitToken = null);

public enum ProfilerLogLevel
{
    Trace,
    Info,
    Warning,
    Error,
    Off,
}

public sealed record RunOptions
{
    public required IReadOnlyList<string> Command { get; init; }
    public bool Profile { get; init; }
    public bool Correlate { get; init; }
    public bool CollectChildren { get; init; }
    public bool ExperimentalGcBarrier { get; init; }
    public string? SnapshotOn { get; init; }
    public string? OutputDirectory { get; init; }
    public string? ProfilerPath { get; init; }
    public ProfilerLogLevel ProfilerLogLevel { get; init; } = ProfilerLogLevel.Warning;
    public bool NeedsProfiler => Profile || Correlate || CollectChildren || ExperimentalGcBarrier || SnapshotOn is not null || ProfilerPath is not null;
    public bool SnapshotOnExit =>
        SnapshotOn?.Split(
            [';', ','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(value => value.Equals("exit", StringComparison.OrdinalIgnoreCase)) == true;
    public bool UseGcBarrier => Correlate && (ExperimentalGcBarrier || SnapshotOnExit);
}

/// <summary>A launched process tree and its profiler connection.</summary>
public sealed partial class RunTarget : IDisposable
{
    private Process? _root;
    private readonly HashSet<string> _seenAllocations = [];

    private string? _captureDir;
    private string? _allocationTemplate;
    private bool _collectChildren;

    public string? AllocationPath { get; private set; }

    private bool _correlate;

    private ProfilerControl? _control;

    private readonly ConcurrentQueue<RunTrigger> _triggerHits = new();
    private sealed record CoherentCaptureWaiter(
        int Pid,
        TaskCompletionSource<(long Gc, string? Error)> Signal);
    private readonly ConcurrentDictionary<string, CoherentCaptureWaiter> _coherentCaptures = new();
    private static readonly TimeSpan CoherentReadyTimeout = TimeSpan.FromSeconds(60);

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
    private readonly TaskCompletionSource<bool> _stdoutDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _stderrDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        lock (_logLock)
        {
            _log = new StreamWriter(LogPath, append: false) { AutoFlush = true };
        }
        _root!.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) _stdoutDrained.TrySetResult(true);
            else WriteLog(e.Data);
        };
        _root.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) _stderrDrained.TrySetResult(true);
            else WriteLog(e.Data);
        };
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
        psi.Environment["SHERLOCK_LOG_LEVEL"] = options.ProfilerLogLevel.ToString().ToLowerInvariant();

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
                _triggerHits.Enqueue(new RunTrigger(pid, fields[2]));
            }
            else if (fields.Length >= 3 && fields[1] == ProfilerControl.ExitCaptureReady)
            {
                _triggerHits.Enqueue(new RunTrigger(pid, "exit", fields[2]));
            }
            else if (fields.Length >= 4 && fields[1] == ProfilerControl.CoherentCaptureReady &&
                     _coherentCaptures.TryGetValue(fields[2], out CoherentCaptureWaiter? ready) &&
                     ready.Pid == pid)
            {
                long gc = long.TryParse(fields[3], out long value) ? value : -1;
                ready.Signal.TrySetResult((gc, null));
            }
            else if (fields.Length >= 4 && fields[1] == ProfilerControl.CoherentCaptureFailed &&
                     _coherentCaptures.TryGetValue(fields[2], out CoherentCaptureWaiter? failed) &&
                     failed.Pid == pid)
            {
                failed.Signal.TrySetResult((-1, fields[3]));
            }
        };
        _control.ClientDisconnected += pid =>
        {
            foreach (CoherentCaptureWaiter capture in _coherentCaptures.Values)
            {
                if (capture.Pid == pid)
                {
                    capture.Signal.TrySetResult((-1, "profiler control channel disconnected"));
                }
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

        lock (_logLock)
        {
            try
            {
                _log?.Flush();
                using var stream = new FileStream(
                    LogPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var lines = new List<string>();
                while (reader.ReadLine() is { } line)
                {
                    lines.Add(line);
                }
                return tail >= lines.Count ? lines : lines[^tail..];
            }
            catch (IOException)
            {
                return [];
            }
            catch (UnauthorizedAccessException)
            {
                return [];
            }
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

    public IReadOnlyList<RunTrigger> PollTriggers()
    {
        if (_root is null)
        {
            return [];
        }
        List<RunTrigger>? hits = null;
        while (_triggerHits.TryDequeue(out RunTrigger? hit))
        {
            (hits ??= []).Add(hit);
        }
        return (IReadOnlyList<RunTrigger>?)hits ?? [];
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

    public CoherentCaptureResult CaptureCoherentSnapshot(int pid, TimeSpan timeout)
    {
        if (!_correlate || _control is null || !IsAlive(pid))
        {
            throw new DumpAnalysisException($"Process {pid} has no live correlation control channel.");
        }

        string token = Guid.NewGuid().ToString("N");
        var signal = new TaskCompletionSource<(long Gc, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_coherentCaptures.TryAdd(token, new CoherentCaptureWaiter(pid, signal)))
        {
            throw new InvalidOperationException("Could not register coherent capture.");
        }

        string? dumpPath = null;
        bool begun = false;
        try
        {
            (bool ok, string[] fields) = Request(pid, ProfilerControl.BeginCoherentCapture, TimeSpan.FromSeconds(10), token);
            if (!ok)
            {
                throw new DumpAnalysisException(fields.FirstOrDefault() ?? "Profiler could not start coherent capture.");
            }
            begun = true;

            TimeSpan readyTimeout = timeout < CoherentReadyTimeout ? timeout : CoherentReadyTimeout;
            (long gc, string? error) result;
            try
            {
                result = signal.Task.WaitAsync(readyTimeout).GetAwaiter().GetResult();
            }
            catch (TimeoutException ex)
            {
                throw new DumpAnalysisException(
                    $"Profiler did not reach the coherent capture barrier within {readyTimeout.TotalSeconds:0} seconds.",
                    ex);
            }
            (long gc, string? error) = result;
            if (error is not null)
            {
                throw new DumpAnalysisException($"Profiler could not park the capture GC: {error}");
            }

            dumpPath = DumpCollector.Collect(pid, DumpKind.Heap);
            (ok, fields) = Request(pid, ProfilerControl.CompleteCoherentCapture, timeout, token);
            if (!ok || fields.Length == 0 || !File.Exists(fields[0]))
            {
                throw new DumpAnalysisException(fields.FirstOrDefault() ?? "Profiler could not complete coherent capture.");
            }

            string provenance = fields[0];
            long completedGc = fields.Length > 1 && long.TryParse(fields[1], out long value) ? value : gc;
            return new CoherentCaptureResult(dumpPath, provenance, completedGc);
        }
        catch
        {
            if (begun && _control.IsConnected(pid))
            {
                _ = Request(pid, ProfilerControl.AbortCoherentCapture, TimeSpan.FromSeconds(5), token);
            }
            if (dumpPath is not null)
            {
                try { File.Delete(dumpPath); } catch { /* best-effort cleanup */ }
            }
            throw;
        }
        finally
        {
            _coherentCaptures.TryRemove(token, out _);
        }
    }

    public (bool Ok, string Detail) ReleaseExitCapture(
        int pid,
        string token,
        TimeSpan timeout)
    {
        if (_control is null || !_control.IsConnected(pid))
        {
            return (false, "profiler control channel disconnected");
        }
        (bool ok, string[] fields) =
            Request(pid, ProfilerControl.ReleaseExitCapture, timeout, token);
        return (ok, fields.FirstOrDefault() ?? (ok ? "released" : "release failed"));
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
        await Task.WhenAll(_stdoutDrained.Task, _stderrDrained.Task)
            .WaitAsync(cancellationToken);
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
