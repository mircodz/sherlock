using System.Diagnostics;
using System.Text.RegularExpressions;
using Sherlock.Core;

namespace Sherlock.Profiler;

public class SherlockDebugger
{
    private ProcessProfiler? _profiler;
    private bool _isRunning = true;
    private bool _isInteractiveMode = true;
    private readonly Dictionary<string, Func<string[], Task>> _commands;
    private readonly string _historyFile;
    private HeapSnapshot? _currentSnapshot;
    private readonly SnapshotManager _snapshotManager;
    
    // Static reference for now - in production this would be injected properly
    private static ProcessProfiler? _activeProfiler;
    
    /// <summary>
    /// Gets the currently active profiler for use by snapshots.
    /// </summary>
    public static ProcessProfiler? GetActiveProfiler() => _activeProfiler;

    static SherlockDebugger()
    {
        // Initialize ReadLine with history
        ReadLine.HistoryEnabled = true;
        ReadLine.AutoCompletionHandler = new AutoCompletionHandler();
    }

    public SherlockDebugger()
    {
        _historyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sherlock_history");
        _snapshotManager = new SnapshotManager();
        LoadHistory();
        
        // Don't auto-load snapshots at startup to avoid complexity
        
        _commands = new Dictionary<string, Func<string[], Task>>
        {
            ["attach"] = AttachCommand,
            ["spawn"] = SpawnCommand,
            ["run"] = RunCommand,
            ["kill"] = KillCommand,
            ["start"] = StartCommand,
            ["stop"] = StopCommand,
            ["info"] = InfoCommand,
            ["heap"] = HeapCommand,
            ["allocations"] = AllocationsCommand,
            ["report"] = ReportCommand,
            ["sites"] = AllocationSitesCommand,
            ["export"] = ExportCommand,
            ["snapshot"] = SnapshotCommand,
            ["snapshots"] = SnapshotsCommand,
            ["switch"] = SwitchCommand,
            ["compare"] = CompareCommand,
            ["analyze"] = AnalyzeCommand,
            ["objects"] = ObjectsCommand,
            ["refs"] = ReferencesCommand,
            ["roots"] = RootsCommand,
            ["gcroots"] = GCRootsCommand,
            ["types"] = TypesCommand,
            ["dominators"] = DominatorsCommand,
            ["inspect"] = InspectCommand,
            ["search"] = SearchHistoryCommand,
            ["clear"] = ClearCommand,
            ["quit"] = QuitCommand,
            ["q"] = QuitCommand,
            ["help"] = HelpCommand,
            ["h"] = HelpCommand
        };
    }

    public async Task RunAsync(bool interactiveMode = true)
    {
        _isInteractiveMode = interactiveMode;
        
        if (_isInteractiveMode)
        {
            Console.WriteLine("Sherlock .NET Memory Profiler");
            Console.WriteLine("Type 'help' for available commands, use ↑↓ arrows for history");
            Console.WriteLine();
        }

        while (_isRunning)
        {
            string? input;
            
            if (_isInteractiveMode)
            {
                Console.Write("(sherlock) ");
                input = ReadLine.Read()?.Trim();
                if (!string.IsNullOrEmpty(input))
                {
                    ReadLine.AddHistory(input);
                    SaveToHistory(input);
                }
            }
            else
            {
                input = Console.ReadLine()?.Trim();
                if (input == null) // EOF reached
                    break;
            }
            
            if (string.IsNullOrEmpty(input))
                continue;

            await ProcessCommandAsync(input);
        }
    }

    public async Task RunBatchAsync(IEnumerable<string> commands)
    {
        _isInteractiveMode = false;
        
        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command) || command.StartsWith("#"))
                continue; // Skip empty lines and comments
                
            Console.WriteLine($"Executing: {command}");
            await ProcessCommandAsync(command);
            
            if (!_isRunning)
                break;
        }
    }

    public async Task RunDirectSpawnAsync(string executable, string? arguments)
    {
        _isInteractiveMode = false;
        
        Console.WriteLine($"Sherlock .NET Memory Profiler - Direct Spawn Mode");
        Console.WriteLine($"Spawning: {executable} {arguments}");
        Console.WriteLine();
        
        _profiler = new ProcessProfiler();
        var process = await _profiler.SpawnProcessAsync(executable, arguments);
        
        if (process != null)
        {
            Console.WriteLine($"Successfully spawned process {process.Id}");
            
            MemoryDataCollector.Instance.StartCollection();
            // Profiling functionality removed
            Console.WriteLine("Started memory profiling");
            
            Console.WriteLine("Press Ctrl+C to stop profiling and generate report...");
            
            // Set up Ctrl+C handler
            Console.CancelKeyPress += async (s, e) =>
            {
                e.Cancel = true;
                await StopAndReport();
            };
            
            // Wait for process to exit or Ctrl+C
            try
            {
                await process.WaitForExitAsync();
                Console.WriteLine("Target process exited");
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C was pressed
            }
            
            await StopAndReport();
        }
        else
        {
            Console.WriteLine($"Failed to spawn process: {executable}");
        }
    }

    public async Task RunDirectAttachAsync(int processId)
    {
        _isInteractiveMode = false;
        
        Console.WriteLine($"Sherlock .NET Memory Profiler - Direct Attach Mode");
        Console.WriteLine($"Attaching to process ID: {processId}");
        Console.WriteLine();
        
        _profiler = new ProcessProfiler();
        
        if (await _profiler.AttachToProcessAsync(processId))
        {
            Console.WriteLine($"Successfully attached to process {processId}");
            
            MemoryDataCollector.Instance.StartCollection();
            // Profiling functionality removed
            Console.WriteLine("Started memory profiling");
            
            Console.WriteLine("Press Ctrl+C to stop profiling and generate report...");
            
            // Set up Ctrl+C handler
            Console.CancelKeyPress += async (s, e) =>
            {
                e.Cancel = true;
                await StopAndReport();
            };
            
            // Wait for Ctrl+C
            try
            {
                await Task.Delay(-1);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C was pressed
            }
            
            await StopAndReport();
        }
        else
        {
            Console.WriteLine($"Failed to attach to process {processId}");
        }
    }

    private async Task StopAndReport()
    {
        if (_profiler != null)
        {
            // Profiling functionality removed
            MemoryDataCollector.Instance.StopCollection();
            Console.WriteLine("Stopped memory profiling");
            
            Console.WriteLine("\n=== Memory Analysis Report ===");
            var report = MemoryDataCollector.Instance.GenerateReport();
            
            Console.WriteLine($"Total Allocations: {FormatBytes(report.TotalAllocations)}");
            Console.WriteLine($"Allocation Count: {report.AllocationCount:N0}");
            
            if (report.LatestHeapStatistics != null)
            {
                var heap = report.LatestHeapStatistics;
                Console.WriteLine($"Final Heap Size: {FormatBytes(heap.TotalHeapSize)}");
            }
            
            Console.WriteLine("\nTop 5 Types by Memory Usage:");
            foreach (var type in report.AllocationsByType.Take(5))
            {
                Console.WriteLine($"  {type.TypeName}: {type.Count:N0} objects, {FormatBytes((ulong)type.TotalSize)}");
            }
            
            // Generate allocation sites report
            Console.WriteLine("\n=== Allocation Sites ===");
            Console.WriteLine("Allocation site reporting removed - use heap snapshot analysis instead");
            
            // Export detailed report
            var fileName = $"sherlock_report_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            Console.WriteLine("Detailed allocation reporting removed - use heap snapshot analysis instead");
            
            _profiler.Dispose();
        }
        
        _isRunning = false;
    }

    public async Task RunInteractiveWithSpawnAsync(string executable, string? arguments)
    {
        Console.WriteLine($"Sherlock .NET Memory Profiler - Interactive Mode");
        Console.WriteLine($"Spawning: {executable} {arguments}");
        Console.WriteLine();
        
        _profiler = new ProcessProfiler();
        var process = await _profiler.SpawnProcessAsync(executable, arguments);
        
        if (process != null)
        {
            Console.WriteLine($"Successfully spawned process {process.Id}");
            
            MemoryDataCollector.Instance.StartCollection();
            // Profiling functionality removed
            Console.WriteLine("Started memory profiling");
            Console.WriteLine();
            
            // Start monitoring the process in the background
            var processMonitorTask = Task.Run(async () =>
            {
                try
                {
                    await process.WaitForExitAsync();
                    Console.WriteLine($"\nTarget process {process.Id} has exited with code {process.ExitCode}");
                    Console.WriteLine("Process data collection stopped, but you can continue analyzing.");
                    
                    // Profiling functionality removed
                    MemoryDataCollector.Instance.StopCollection();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\nError monitoring process: {ex.Message}");
                }
            });
            
            // Enter interactive mode
            Console.WriteLine("Type 'help' for available commands.");
            await RunAsync(interactiveMode: true);
        }
        else
        {
            Console.WriteLine($"Failed to spawn process: {executable}");
            Console.WriteLine("Entering interactive mode anyway...");
            await RunAsync(interactiveMode: true);
        }
    }

    private async Task ProcessCommandAsync(string input)
    {
        var parts = ParseCommand(input);
        if (parts.Length == 0) return;

        var command = parts[0].ToLower();
        var args = parts.Skip(1).ToArray();

        if (_commands.TryGetValue(command, out var handler))
        {
            try
            {
                await handler(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error executing command: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"Unknown command: {command}. Type 'help' for available commands.");
        }
    }

    private string[] ParseCommand(string input)
    {
        var matches = Regex.Matches(input, @"[\""].+?[\""]|[^ ]+")
            .Cast<Match>()
            .Select(m => m.Value.Trim('"'))
            .ToArray();
        return matches;
    }

    private async Task AttachCommand(string[] args)
    {
        if (args.Length != 1 || !int.TryParse(args[0], out var processId))
        {
            Console.WriteLine("Usage: attach <process_id>");
            return;
        }

        _profiler?.Dispose();
        _profiler = new ProcessProfiler();

        if (await _profiler.AttachToProcessAsync(processId))
        {
            Console.WriteLine($"Successfully attached to process {processId}");
        }
        else
        {
            Console.WriteLine($"Failed to attach to process {processId}");
            _profiler.Dispose();
            _profiler = null;
        }
    }

    private async Task SpawnCommand(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: spawn <executable> [arguments...]");
            return;
        }

        var executable = args[0];
        var arguments = args.Length > 1 ? string.Join(" ", args.Skip(1)) : null;

        _profiler?.Dispose();
        _profiler = new ProcessProfiler();

        var process = await _profiler.SpawnProcessAsync(executable, arguments);
        if (process != null)
        {
            Console.WriteLine($"Successfully spawned process {process.Id}: {executable}");
        }
        else
        {
            Console.WriteLine($"Failed to spawn process: {executable}");
            _profiler.Dispose();
            _profiler = null;
        }
    }

    private async Task RunCommand(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: run <executable> [arguments]");
            return;
        }

        try
        {
            var executable = args[0];
            var arguments = string.Join(" ", args.Skip(1));

            Console.WriteLine($"Running '{executable}' with arguments: {arguments}");
            
            // Clean up any existing profiler
            if (_profiler != null)
            {
                if (_profiler.IsAttached)
                {
                    Console.WriteLine("Stopping current profiling session...");
                    // Profiling functionality removed
                }
                _profiler.Dispose();
            }

            _profiler = new ProcessProfiler();
            var process = await _profiler.SpawnProcessAsync(executable, arguments);
            
            if (process != null)
            {
                Console.WriteLine($"✓ Process spawned with PID: {process.Id}");
                Console.WriteLine("✓ Starting memory profiling automatically...");
                
                MemoryDataCollector.Instance.StartCollection();
                // Profiling functionality removed
                _activeProfiler = _profiler; // Set the active profiler for snapshots
                Console.WriteLine("✓ Memory profiling started");
                Console.WriteLine("Use 'stop' to stop profiling, 'kill' to terminate the process");
            }
            else
            {
                Console.WriteLine("Failed to spawn process");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error running process: {ex.Message}");
        }
    }

    private async Task KillCommand(string[] args)
    {
        if (_profiler?.ProcessId == null)
        {
            Console.WriteLine("No process is currently being profiled.");
            return;
        }

        try
        {
            var processId = _profiler.ProcessId.Value;
            
            // Stop profiling first
            if (_profiler.IsAttached)
            {
                Console.WriteLine("Stopping profiling...");
                // Profiling functionality removed
                MemoryDataCollector.Instance.StopCollection();
            }

            // Try to get the process and kill it
            try
            {
                var process = Process.GetProcessById(processId);
                Console.WriteLine($"Terminating process {processId} ({process.ProcessName})...");
                
                // Try graceful shutdown first
                if (!process.CloseMainWindow())
                {
                    Console.WriteLine("Graceful shutdown failed, force killing...");
                    process.Kill();
                }
                
                // Wait a bit for termination
                if (process.WaitForExit(5000))
                {
                    Console.WriteLine($"✓ Process {processId} terminated successfully");
                }
                else
                {
                    Console.WriteLine($"⚠ Process {processId} may still be running");
                }
            }
            catch (ArgumentException)
            {
                Console.WriteLine($"Process {processId} is no longer running");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error killing process: {ex.Message}");
            }
            finally
            {
                // Clean up profiler
                _profiler.Dispose();
                _profiler = null;
                Console.WriteLine("Profiler cleanup completed");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during kill operation: {ex.Message}");
        }
    }

    private async Task StartCommand(string[] args)
    {
        if (_profiler == null || !_profiler.IsAttached)
        {
            Console.WriteLine("No process attached. Use 'attach' or 'spawn' first.");
            return;
        }

        MemoryDataCollector.Instance.StartCollection();
        // Profiling functionality removed
        _activeProfiler = _profiler; // Set the active profiler for snapshots
        Console.WriteLine("Started memory profiling");
    }

    private async Task StopCommand(string[] args)
    {
        if (_profiler == null)
        {
            Console.WriteLine("No active profiling session");
            return;
        }

        // Profiling functionality removed
        MemoryDataCollector.Instance.StopCollection();
        _activeProfiler = null; // Clear the active profiler
        Console.WriteLine("Stopped memory profiling");
    }

    private async Task InfoCommand(string[] args)
    {
        if (_profiler == null)
        {
            Console.WriteLine("No profiler session");
        }
        else
        {
            var processId = _profiler.ProcessId ?? -1;
            Console.WriteLine($"Process ID: {processId}");
            Console.WriteLine($"Attached: {_profiler.IsAttached}");
            
            // Try to get process info
            if (processId > 0)
            {
                try
                {
                    var process = Process.GetProcessById(processId);
                    Console.WriteLine($"Process Name: {process.ProcessName}");
                    Console.WriteLine($"Process Status: Running");
                    Console.WriteLine($"Start Time: {process.StartTime:yyyy-MM-dd HH:mm:ss}");
                }
                catch (ArgumentException)
                {
                    Console.WriteLine("Process Status: ⚠ Process no longer exists");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Process Status: Error - {ex.Message}");
                }
            }
        }
        
        var allocations = MemoryDataCollector.Instance.GetAllocations();
        var heapStats = MemoryDataCollector.Instance.GetHeapStatistics();
        
        Console.WriteLine($"Recorded allocations: {allocations.Count:N0}");
        Console.WriteLine($"Heap statistics snapshots: {heapStats.Count:N0}");
        
        if (allocations.Count > 0 || heapStats.Count > 0)
        {
            Console.WriteLine("Data available for analysis (use 'report', 'sites', 'allocations' commands)");
        }
        else
        {
            Console.WriteLine("No data collected yet. Try running 'start' to begin profiling.");
        }
    }

    private async Task HeapCommand(string[] args)
    {
        var heapStats = MemoryDataCollector.Instance.GetHeapStatistics();
        if (!heapStats.Any())
        {
            Console.WriteLine("No heap statistics available");
            return;
        }

        var latest = heapStats.Last();
        Console.WriteLine($"Latest Heap Statistics (at {latest.Timestamp:HH:mm:ss.fff}):");
        Console.WriteLine($"  Gen 0: {FormatBytes(latest.GenerationSize0)}");
        Console.WriteLine($"  Gen 1: {FormatBytes(latest.GenerationSize1)}");
        Console.WriteLine($"  Gen 2: {FormatBytes(latest.GenerationSize2)}");
        Console.WriteLine($"  Gen 3 (LOH): {FormatBytes(latest.GenerationSize3)}");
        Console.WriteLine($"  Total Heap: {FormatBytes(latest.TotalHeapSize)}");
        Console.WriteLine($"  Finalization Promoted: {FormatBytes(latest.FinalizationPromotedSize)}");
    }

    private async Task AllocationsCommand(string[] args)
    {
        var allocations = MemoryDataCollector.Instance.GetAllocations();
        if (!allocations.Any())
        {
            Console.WriteLine("No allocations recorded");
            return;
        }

        var limit = 20;
        if (args.Length > 0 && int.TryParse(args[0], out var userLimit))
            limit = userLimit;

        Console.WriteLine($"Recent Allocations (showing last {limit}):");
        Console.WriteLine("Time\t\tType\t\t\tSize\tThread");
        Console.WriteLine(new string('-', 80));

        foreach (var allocation in allocations.TakeLast(limit))
        {
            Console.WriteLine($"{allocation.Timestamp:HH:mm:ss.fff}\t{allocation.TypeName,-20}\t{FormatBytes(allocation.AllocationAmount)}\t{allocation.ThreadId}");
        }
    }

    private async Task ReportCommand(string[] args)
    {
        var report = MemoryDataCollector.Instance.GenerateReport();
        
        Console.WriteLine("Memory Analysis Report");
        Console.WriteLine("=====================");
        Console.WriteLine($"Total Allocations: {FormatBytes(report.TotalAllocations)}");
        Console.WriteLine($"Allocation Count: {report.AllocationCount:N0}");
        Console.WriteLine();

        if (report.LatestHeapStatistics != null)
        {
            var heap = report.LatestHeapStatistics;
            Console.WriteLine($"Current Heap Size: {FormatBytes(heap.TotalHeapSize)}");
            Console.WriteLine();
        }

        Console.WriteLine("Top Allocations by Type:");
        Console.WriteLine("Type\t\t\t\tCount\tTotal Size\tAvg Size");
        Console.WriteLine(new string('-', 80));

        foreach (var type in report.AllocationsByType.Take(10))
        {
            var avgSize = type.Count > 0 ? (ulong)(type.TotalSize / type.Count) : 0;
            Console.WriteLine($"{type.TypeName,-30}\t{type.Count:N0}\t{FormatBytes((ulong)type.TotalSize)}\t{FormatBytes(avgSize)}");
        }
    }

    private async Task ClearCommand(string[] args)
    {
        MemoryDataCollector.Instance.Clear();
        Console.WriteLine("Cleared all collected data");
    }

    private async Task QuitCommand(string[] args)
    {
        _profiler?.Dispose();
        _currentSnapshot?.Dispose();
        _isRunning = false;
        Console.WriteLine("Goodbye!");
    }

    private Task AllocationSitesCommand(string[] args)
    {
        Console.WriteLine("Allocation site reporting removed - use heap snapshot analysis instead");
        return Task.CompletedTask;
    }

    private Task ExportCommand(string[] args)
    {
        var fileName = args.Length > 0 ? args[0] : $"sherlock_export_{DateTime.Now:yyyyMMdd_HHmmss}.json";
        Console.WriteLine("Detailed allocation reporting removed - use heap snapshot analysis instead");
        return Task.CompletedTask;
    }

    private async Task SnapshotCommand(string[] args)
    {
        if (_profiler == null || _profiler.ProcessId == null)
        {
            Console.WriteLine("No process attached. Use 'attach' or 'spawn' first.");
            return;
        }

        try
        {
            var snapshotId = await _snapshotManager.TakeSnapshotAsync(_profiler.ProcessId.Value);
            var snapshot = await _snapshotManager.SwitchToSnapshotAsync(snapshotId);
            
            if (snapshot != null)
            {
                _currentSnapshot?.Dispose();
                _currentSnapshot = snapshot;
                Console.WriteLine($"Current snapshot: {_currentSnapshot.TotalObjects:N0} objects, {FormatBytes((ulong)_currentSnapshot.TotalMemory)}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to take snapshot: {ex.Message}");
        }
    }

    private async Task SnapshotsCommand(string[] args)
    {
        _snapshotManager.ListSnapshots();
    }

    private async Task SwitchCommand(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: switch <snapshot-id>");
            return;
        }

        if (!int.TryParse(args[0], out var snapshotId))
        {
            Console.WriteLine("Invalid snapshot ID. Must be a number.");
            return;
        }

        try
        {
            var snapshot = await _snapshotManager.SwitchToSnapshotAsync(snapshotId);
            
            if (snapshot != null)
            {
                _currentSnapshot?.Dispose();
                _currentSnapshot = snapshot;
                Console.WriteLine($"Switched to snapshot #{snapshotId}: {_currentSnapshot.TotalObjects:N0} objects, {FormatBytes((ulong)_currentSnapshot.TotalMemory)}");
            }
            else
            {
                Console.WriteLine($"Failed to load snapshot #{snapshotId}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to switch to snapshot #{snapshotId}: {ex.Message}");
        }
    }

    private async Task CompareCommand(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: compare <snapshot-id1> <snapshot-id2>");
            return;
        }

        if (!int.TryParse(args[0], out var id1) || !int.TryParse(args[1], out var id2))
        {
            Console.WriteLine("Invalid snapshot IDs. Must be numbers.");
            return;
        }

        try
        {
            await _snapshotManager.CompareSnapshotsAsync(id1, id2);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to compare snapshots: {ex.Message}");
        }
    }


    private async Task AnalyzeCommand(string[] args)
    {
        if (_currentSnapshot == null)
        {
            Console.WriteLine("No snapshot available. Use 'snapshot' command first.");
            return;
        }

        // Ensure the snapshot is analyzed
        await _currentSnapshot.AnalyzeAsync();

        var report = _currentSnapshot.GenerateReport();
        
        Console.WriteLine($"Heap Analysis Report (taken {report.SnapshotTime:yyyy-MM-dd HH:mm:ss})");
        Console.WriteLine("==================================================");
        Console.WriteLine($"Total Objects: {report.TotalObjects:N0}");
        Console.WriteLine($"Total Memory: {FormatBytes((ulong)report.TotalMemory)}");
        Console.WriteLine();

        Console.WriteLine("Top Types by Retained Size:");
        Console.WriteLine("Type                          Count      Total Size    Retained Size");
        Console.WriteLine(new string('-', 80));
        
        foreach (var type in report.TypeStatistics.Take(10))
        {
            Console.WriteLine($"{type.TypeName,-28} {type.InstanceCount,8:N0} {FormatBytes((ulong)type.TotalSize),12} {FormatBytes((ulong)type.TotalRetainedSize),12}");
        }

        Console.WriteLine();
        Console.WriteLine("Generation Statistics:");
        foreach (var gen in report.GenerationStatistics.OrderBy(g => g.Generation))
        {
            Console.WriteLine($"  Gen {gen.Generation}: {gen.ObjectCount:N0} objects, {FormatBytes((ulong)gen.TotalSize)}");
        }
    }

    private Task ObjectsCommand(string[] args)
    {
        if (_currentSnapshot == null)
        {
            Console.WriteLine("No snapshot available. Use 'snapshot' command first.");
            return Task.CompletedTask;
        }

        if (args.Length == 0)
        {
            Console.WriteLine("Usage: objects <typename> [count]");
            Console.WriteLine("       objects address <hex-address>");
            return Task.CompletedTask;
        }


        if (args[0].Equals("address", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
        {
            if (TryParseAddress(args[1], out var address))
            {
                ShowObjectDetails(address, shallow: true);
            }
            else
            {
                Console.WriteLine("Invalid hex address format. Use format: FFFFFFFF or 0xFFFFFFFF");
            }
            return Task.CompletedTask;
        }

        var typeName = args[0];
        var count = args.Length > 1 && int.TryParse(args[1], out var c) ? c : 10;
        
        var objects = _currentSnapshot.GetObjectsByType(typeName).Take(count).ToList();
        
        if (!objects.Any())
        {
            Console.WriteLine($"No objects found of type '{typeName}'");
            return Task.CompletedTask;
        }

        Console.WriteLine($"Objects of type '{typeName}' (showing {objects.Count}):");
        Console.WriteLine("Address            Size       Retained   Gen  Refs");
        Console.WriteLine(new string('-', 60));
        
        foreach (var obj in objects)
        {
            Console.WriteLine($"0x{obj.Address:X12} {FormatBytes(obj.Size),10} {FormatBytes(obj.RetainedSize),10} {obj.Generation,3} {obj.References.Count,4}");
        }
        
        return Task.CompletedTask;
    }

    private Task InspectCommand(string[] args)
    {
        if (_currentSnapshot == null)
        {
            Console.WriteLine("No snapshot available. Use 'snapshot' command first.");
            return Task.CompletedTask;
        }

        if (args.Length == 0)
        {
            Console.WriteLine("Usage: inspect <hex-address> [-depth <N>]");
            Console.WriteLine("       inspect <typename> <index> [-depth <N>]");
            Console.WriteLine();
            Console.WriteLine("Shows detailed object structure with fields, references, and sizes.");
            Console.WriteLine("Options:");
            Console.WriteLine("  -depth N    Recursively inspect referenced objects to depth N (default: 2)");
            return Task.CompletedTask;
        }


        var depth = 2; // Default depth
        
        // Parse depth argument
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-depth" && int.TryParse(args[i + 1], out var d))
            {
                depth = Math.Max(0, Math.Min(d, 5)); // Limit depth to prevent excessive output
                break;
            }
        }

        // Try to parse as hex address first
        if (TryParseAddress(args[0], out var address))
        {
            ShowObjectDetails(address, shallow: false, depth: depth);
        }
        // Try typename + index format
        else if (args.Length >= 2 && int.TryParse(args[1], out var index))
        {
            var typeName = args[0];
            var objects = _currentSnapshot.GetObjectsByType(typeName).Skip(index).Take(1).ToList();
            if (objects.Any())
            {
                ShowObjectDetails(objects[0].Address, shallow: false, depth: depth);
            }
            else
            {
                Console.WriteLine($"No object found at index {index} for type '{typeName}'");
            }
        }
        else
        {
            Console.WriteLine("Invalid format. Use hex address (e.g., FFFFFFFF or 0xFFFFFFFF) or typename + index.");
        }

        return Task.CompletedTask;
    }

    private void ShowObjectDetails(ulong address, bool shallow = false, int depth = 0, int currentDepth = 0, HashSet<ulong>? visited = null)
    {
        visited ??= new HashSet<ulong>();
        
        if (visited.Contains(address) || currentDepth > depth)
        {
            if (currentDepth > depth)
                Console.WriteLine($"{"".PadLeft(currentDepth * 2)}... (max depth reached)");
            else
                Console.WriteLine($"{"".PadLeft(currentDepth * 2)}... (circular reference)");
            return;
        }
        
        visited.Add(address);
        var obj = _currentSnapshot.GetObject(address);
        
        if (obj == null)
        {
            Console.Write($"{"".PadLeft(currentDepth * 2)}Object not found at address ");
            ColorTheme.WriteAddress(address);
            Console.WriteLine();
            return;
        }

        var indent = "".PadLeft(currentDepth * 2);
        
        ColorTheme.WriteObjectHeader(obj.Address, obj.TypeName, indent);
        ColorTheme.WriteSizeLine("Shallow Size", FormatBytes(obj.Size), indent);
        ColorTheme.WriteSizeLine("Retained Size", FormatBytes(obj.RetainedSize), indent);
        ColorTheme.WriteTreeStructure($"{indent}├─ Generation: ");
        Console.WriteLine(obj.Generation);
        
        // Show allocation information if available
        if (obj.AllocationInfo != null)
        {
            ColorTheme.WriteTreeStructure($"{indent}├─ Allocated: ");
            Console.WriteLine($"{obj.AllocationInfo.Timestamp:HH:mm:ss.fff} on thread {obj.AllocationInfo.ThreadId}");
            
            if (obj.AllocationInfo.ParsedStackTrace.Any())
            {
                var hasMore = obj.Fields.Any() || obj.References.Any() && !shallow;
                var allocPrefix = hasMore ? "├─" : "└─";
                ColorTheme.WriteTreeStructure($"{indent}{allocPrefix} Stack Trace:");
                Console.WriteLine();
                
                var frameCount = Math.Min(obj.AllocationInfo.ParsedStackTrace.Count, 10); // Show top 10 frames
                for (int i = 0; i < frameCount; i++)
                {
                    var frame = obj.AllocationInfo.ParsedStackTrace[i];
                    var isLastFrame = i == frameCount - 1;
                    var framePrefix = isLastFrame && !hasMore ? "└─" : "├─";
                    
                    ColorTheme.WriteTreeStructure($"{indent}│  {framePrefix} ");
                    ColorTheme.WriteTypeName(frame.ClassName);
                    Console.Write(".");
                    ColorTheme.WriteFieldName(frame.MethodName);
                    Console.Write("()");
                    
                    if (!string.IsNullOrEmpty(frame.FileName) && frame.LineNumber > 0)
                    {
                        Console.Write(" in ");
                        ColorTheme.WriteInfo(Path.GetFileName(frame.FileName));
                        Console.Write(":");
                        ColorTheme.WriteInfo(frame.LineNumber.ToString());
                    }
                    Console.WriteLine();
                }
                
                if (obj.AllocationInfo.ParsedStackTrace.Count > 10)
                {
                    ColorTheme.WriteTreeStructure($"{indent}│     ... {obj.AllocationInfo.ParsedStackTrace.Count - 10} more frames");
                    Console.WriteLine();
                }
            }
        }
        
        // Show field values with details
        if (obj.Fields.Any())
        {
            var hasReferences = obj.References.Any() && !shallow;
            var fieldsPrefix = hasReferences ? "├─" : "└─";
            ColorTheme.WriteTreeStructure($"{indent}{fieldsPrefix} Fields ({obj.Fields.Count}):");
            Console.WriteLine();
            
            var fieldNum = 0;
            foreach (var field in obj.Fields)
            {
                fieldNum++;
                var isLastField = fieldNum == obj.Fields.Count;
                var fieldPrefix = isLastField && !hasReferences ? "└─" : "├─";
                
                ColorTheme.WriteFieldLine(field.Key, FormatFieldValue(field.Value), $"{indent}│  ", fieldPrefix);
            }
        }
        
        // Show references with details
        if (obj.References.Any() && !shallow)
        {
            ColorTheme.WriteTreeStructure($"{indent}└─ References ({obj.References.Count}):");
            Console.WriteLine();
            
            var refNum = 0;
            var referencesToShow = obj.References.Take(10).ToList(); // Limit to first 10 refs
            foreach (var reference in referencesToShow)
            {
                refNum++;
                var isLast = refNum == referencesToShow.Count;
                var prefix = isLast ? "└─" : "├─";
                
                var targetObj = _currentSnapshot.GetObject(reference.TargetAddress);
                var targetInfo = targetObj != null ? 
                    $"{targetObj.TypeName} ({FormatBytes(targetObj.Size)})" : 
                    "Unknown";
                
                ColorTheme.WriteReferenceLine(reference.FieldName, reference.TargetAddress, targetInfo, $"{indent}   ", prefix);
                
                // Recursively show referenced objects if depth allows
                if (currentDepth < depth && targetObj != null)
                {
                    ShowObjectDetails(reference.TargetAddress, shallow: false, depth: depth, currentDepth: currentDepth + 1, visited: visited);
                }
            }
            
            if (obj.References.Count > 10)
            {
                Console.WriteLine($"{indent}      ... and {obj.References.Count - 10} more references");
            }
        }
        
        Console.WriteLine(); // Add spacing between objects
    }
    
    private string FormatFieldValue(object? value)
    {
        return value switch
        {
            null => "null",
            string s when s.Length > 50 => $"\"{s[..47]}...\" (string, {s.Length} chars)",
            string s => $"\"{s}\" (string)",
            byte[] arr => $"byte[{arr.Length}] ({FormatBytes((ulong)arr.Length)})",
            Array arr => $"{arr.GetType().GetElementType()?.Name}[{arr.Length}]",
            _ when value.GetType().IsPrimitive => $"{value} ({value.GetType().Name})",
            _ => $"{value} ({value.GetType().Name})"
        };
    }

    private Task ReferencesCommand(string[] args)
    {
        if (_currentSnapshot == null)
        {
            Console.WriteLine("No snapshot available. Use 'snapshot' command first.");
            return Task.CompletedTask;
        }

        if (args.Length == 0)
        {
            Console.WriteLine("Usage: refs <hex-address> [incoming|outgoing]");
            return Task.CompletedTask;
        }

        if (!TryParseAddress(args[0], out var address))
        {
            Console.WriteLine("Invalid hex address format. Use format: FFFFFFFF or 0xFFFFFFFF");
            return Task.CompletedTask;
        }

        var direction = args.Length > 1 ? args[1].ToLower() : "outgoing";
        var obj = _currentSnapshot.GetObject(address);
        
        if (obj == null)
        {
            Console.WriteLine($"Object not found at address 0x{address:X}");
            return Task.CompletedTask;
        }

        if (direction == "incoming")
        {
            var incoming = _currentSnapshot.GetIncomingReferences(address).ToList();
            Console.WriteLine($"Incoming references to 0x{address:X} ({incoming.Count} total):");
            Console.WriteLine("Source Address     Field Name           Type");
            Console.WriteLine(new string('-', 60));
            
            foreach (var reference in incoming.Take(20))
            {
                Console.WriteLine($"0x{reference.SourceAddress:X12} {reference.FieldName,-18} {reference.TypeName}");
            }
        }
        else
        {
            Console.WriteLine($"Outgoing references from 0x{address:X} ({obj.References.Count} total):");
            Console.WriteLine("Target Address     Field Name           Type");
            Console.WriteLine(new string('-', 60));
            
            foreach (var reference in obj.References.Take(20))
            {
                Console.WriteLine($"0x{reference.TargetAddress:X12} {reference.FieldName,-18} {reference.TypeName}");
            }
        }
        
        return Task.CompletedTask;
    }

    private Task RootsCommand(string[] args)
    {
        if (_currentSnapshot == null)
        {
            Console.WriteLine("No snapshot available. Use 'snapshot' command first.");
            return Task.CompletedTask;
        }

        if (args.Length == 0)
        {
            Console.WriteLine("Usage: roots <hex-address>");
            return Task.CompletedTask;
        }

        if (!TryParseAddress(args[0], out var address))
        {
            Console.WriteLine("Invalid hex address format. Use format: FFFFFFFF or 0xFFFFFFFF");
            return Task.CompletedTask;
        }

        var obj = _currentSnapshot.GetObject(address);
        if (obj == null)
        {
            Console.WriteLine($"Object not found at address 0x{address:X}");
            return Task.CompletedTask;
        }

        if (!obj.GCRootPaths.Any())
        {
            Console.WriteLine($"No GC root paths found for object at 0x{address:X}");
            return Task.CompletedTask;
        }

        Console.WriteLine($"GC Root paths for object at 0x{address:X}:");
        Console.WriteLine("Root Kind          Root Address       Root Name");
        Console.WriteLine(new string('-', 60));
        
        foreach (var rootPath in obj.GCRootPaths)
        {
            Console.WriteLine($"{rootPath.RootKind,-16} 0x{rootPath.RootAddress:X12} {rootPath.RootName}");
        }
        
        return Task.CompletedTask;
    }

    private Task GCRootsCommand(string[] args)
    {
        if (_currentSnapshot == null)
        {
            Console.WriteLine("No snapshot available. Use 'snapshot' command first.");
            return Task.CompletedTask;
        }

        var limit = 50; // Default limit
        if (args.Length > 0 && int.TryParse(args[0], out var userLimit))
        {
            limit = Math.Max(1, Math.Min(userLimit, 1000)); // Between 1 and 1000
        }

        Console.WriteLine("Enumerating GC roots from CLR...");
        Console.WriteLine($"Root Kind          Object Address     Object Type");
        Console.WriteLine(new string('-', 80));

        var rootCount = 0;
        var validRoots = 0;

        try
        {
            if (_currentSnapshot.Runtime?.Heap == null)
            {
                Console.WriteLine("No heap available for GC root enumeration");
                return Task.CompletedTask;
            }

            foreach (var root in _currentSnapshot.Runtime.Heap.EnumerateRoots())
            {
                rootCount++;
                
                if (root.Object != 0)
                {
                    var obj = _currentSnapshot.GetObject(root.Object);
                    var objType = obj?.TypeName ?? "Unknown";
                    
                    Console.Write($"{root.RootKind,-16} ");
                    ColorTheme.WriteAddress(root.Object);
                    Console.Write(" ");
                    ColorTheme.WriteTypeName(objType);
                    Console.WriteLine();
                    
                    validRoots++;
                    
                    if (validRoots >= limit)
                    {
                        break;
                    }
                }
            }
            
            Console.WriteLine();
            Console.WriteLine($"Showing {validRoots} of {rootCount} total GC roots (use 'gcroots <number>' to see more)");
            
            // Also show objects that have GC root paths populated
            var objectsWithRoots = _currentSnapshot.Objects.Values
                .Where(o => o.GCRootPaths.Count > 0)
                .Take(10)
                .ToList();
                
            if (objectsWithRoots.Any())
            {
                Console.WriteLine();
                Console.WriteLine("Objects with populated GC root paths:");
                foreach (var obj in objectsWithRoots)
                {
                    Console.Write("  ");
                    ColorTheme.WriteAddress(obj.Address);
                    Console.Write(" ");
                    ColorTheme.WriteTypeName(obj.TypeName);
                    Console.WriteLine($" ({obj.GCRootPaths.Count} root paths)");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error enumerating GC roots: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private Task TypesCommand(string[] args)
    {
        if (_currentSnapshot == null)
        {
            Console.WriteLine("No snapshot available. Use 'snapshot' command first.");
            return Task.CompletedTask;
        }

        var maxTypes = 50; // Default limit
        var sortBy = "count"; // Default sort by instance count
        
        // Parse arguments
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var limit))
            {
                maxTypes = limit;
                i++; // Skip the next argument since we consumed it
            }
            else if (args[i] == "-sort" && i + 1 < args.Length)
            {
                sortBy = args[i + 1].ToLower();
                i++; // Skip the next argument since we consumed it
            }
        }

        Console.WriteLine($"Showing up to {maxTypes} types (sorted by {sortBy}):");
        Console.WriteLine("=====================================");
        
        var typeStats = _currentSnapshot.TypeIndex
            .Select(kvp => new { 
                TypeName = kvp.Key, 
                Count = kvp.Value.Count, 
                TotalSize = kvp.Value.Sum(addr => _currentSnapshot.Objects.ContainsKey(addr) ? (long)_currentSnapshot.Objects[addr].Size : 0)
            })
            .OrderByDescending(t => sortBy == "size" ? t.TotalSize : t.Count)
            .Take(maxTypes);

        var rank = 1;
        foreach (var typeStat in typeStats)
        {
            Console.WriteLine($"{rank,3}. {typeStat.TypeName}");
            Console.WriteLine($"     Instances: {typeStat.Count:N0}");
            Console.WriteLine($"     Total Size: {FormatBytes((ulong)typeStat.TotalSize)}");
            Console.WriteLine();
            rank++;
        }

        var totalTypes = _currentSnapshot.TypeIndex.Count;
        Console.WriteLine($"Total unique types in snapshot: {totalTypes:N0}");
        Console.WriteLine();
        Console.WriteLine("Usage: types [-limit <number>] [-sort count|size]");
        
        return Task.CompletedTask;
    }

    private Task DominatorsCommand(string[] args)
    {
        if (_currentSnapshot == null)
        {
            Console.WriteLine("No snapshot available. Use 'snapshot' command first.");
            return Task.CompletedTask;
        }


        var maxItems = 25; // Default limit - more than analyze command
        var minSize = 1024L; // Default minimum size (1KB)
        
        // Parse arguments
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out var limit))
            {
                maxItems = limit;
                i++; // Skip the next argument since we consumed it
            }
            else if (args[i] == "-minsize" && i + 1 < args.Length && long.TryParse(args[i + 1], out var size))
            {
                minSize = size;
                i++; // Skip the next argument since we consumed it
            }
        }

        Console.WriteLine($"Top {maxItems} Memory Dominators (min size: {FormatBytes((ulong)minSize)}):");
        Console.WriteLine("=====================================");
        
        // Debug: Show some type statistics first
        if (args.Contains("-debug"))
        {
            Console.WriteLine("=== DEBUG INFO ===");
            var allTypeStats = _currentSnapshot.TypeIndex
                .Select(kvp => new { 
                    TypeName = kvp.Key, 
                    Count = kvp.Value.Count, 
                    TotalShallowSize = kvp.Value.Sum(addr => _currentSnapshot.Objects.ContainsKey(addr) ? (long)_currentSnapshot.Objects[addr].Size : 0),
                    TotalRetainedSize = kvp.Value.Sum(addr => _currentSnapshot.Objects.ContainsKey(addr) ? (long)_currentSnapshot.Objects[addr].RetainedSize : 0)
                })
                .OrderByDescending(t => t.Count)
                .Take(20);
                
            foreach (var stat in allTypeStats)
            {
                Console.WriteLine($"  {stat.TypeName}: {stat.Count} instances, Shallow: {FormatBytes((ulong)stat.TotalShallowSize)}, Retained: {FormatBytes((ulong)stat.TotalRetainedSize)}");
            }
            Console.WriteLine("==================");
        }
        
        var topTypes = _currentSnapshot.TypeIndex
            .Select(kvp => new { 
                TypeName = kvp.Key, 
                Count = kvp.Value.Count, 
                TotalShallowSize = kvp.Value.Sum(addr => _currentSnapshot.Objects.ContainsKey(addr) ? (long)_currentSnapshot.Objects[addr].Size : 0),
                TotalRetainedSize = kvp.Value.Sum(addr => _currentSnapshot.Objects.ContainsKey(addr) ? (long)_currentSnapshot.Objects[addr].RetainedSize : 0)
            })
            .Where(t => t.TotalRetainedSize >= minSize)
            .OrderByDescending(t => t.TotalRetainedSize)
            .Take(maxItems);

        var rank = 1;
        var totalShownSize = 0L;
        var totalShownInstances = 0;
        
        foreach (var typeStat in topTypes)
        {
            var avgRetainedSize = typeStat.Count > 0 ? typeStat.TotalRetainedSize / typeStat.Count : 0;
            var avgShallowSize = typeStat.Count > 0 ? typeStat.TotalShallowSize / typeStat.Count : 0;
            Console.WriteLine($"{rank,3}. {typeStat.TypeName}");
            Console.WriteLine($"     Retained: {FormatBytes((ulong)typeStat.TotalRetainedSize)} ({typeStat.Count:N0} instances)");
            Console.WriteLine($"     Shallow: {FormatBytes((ulong)typeStat.TotalShallowSize)}");
            Console.WriteLine($"     Avg Retained: {FormatBytes((ulong)avgRetainedSize)} per instance");
            
            totalShownSize += typeStat.TotalRetainedSize;
            totalShownInstances += typeStat.Count;
            rank++;
        }

        var totalHeapRetainedSize = _currentSnapshot.Objects.Values.Sum(o => (long)o.RetainedSize);
        var totalHeapShallowSize = _currentSnapshot.Objects.Values.Sum(o => (long)o.Size);
        var percentageShown = totalHeapRetainedSize > 0 ? (double)totalShownSize / totalHeapRetainedSize * 100 : 0;
        
        Console.WriteLine();
        Console.WriteLine($"Showing {totalShownInstances:N0} instances with retained size: {FormatBytes((ulong)totalShownSize)}");
        Console.WriteLine($"This represents {percentageShown:F1}% of total heap retained size");
        Console.WriteLine($"Total heap shallow size: {FormatBytes((ulong)totalHeapShallowSize)}");
        Console.WriteLine();
        Console.WriteLine("Usage: dominators [-limit <number>] [-minsize <bytes>] [-debug]");
        
        return Task.CompletedTask;
    }

    private Task SearchHistoryCommand(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: search <term>");
            Console.WriteLine("       search -i <term>  (interactive mode)");
            Console.WriteLine();
            Console.WriteLine("Searches command history for entries containing the specified term.");
            Console.WriteLine("In interactive mode, you can select and execute a command from the results.");
            return Task.CompletedTask;
        }

        var interactive = args[0] == "-i";
        var searchTerm = interactive && args.Length > 1 ? args[1] : args[0];
        
        if (interactive && args.Length < 2)
        {
            Console.WriteLine("Interactive mode requires a search term. Usage: search -i <term>");
            return Task.CompletedTask;
        }

        var history = ReadLine.GetHistory();
        var matches = history
            .Where(entry => entry.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            .Reverse() // Most recent first
            .Select((entry, index) => new { Index = index + 1, Command = entry })
            .ToList();

        if (matches.Count == 0)
        {
            Console.WriteLine($"No history entries found containing '{searchTerm}'");
            return Task.CompletedTask;
        }

        Console.WriteLine($"Found {matches.Count} matching commands:");
        Console.WriteLine();

        foreach (var match in matches)
        {
            Console.WriteLine($"{match.Index,3}: {match.Command}");
        }

        if (interactive)
        {
            Console.WriteLine();
            Console.Write("Select command number to execute (or Enter to cancel): ");
            var selection = Console.ReadLine()?.Trim();
            
            if (int.TryParse(selection, out var selectedIndex) && 
                selectedIndex >= 1 && selectedIndex <= matches.Count)
            {
                var selectedCommand = matches[selectedIndex - 1].Command;
                Console.WriteLine($"Executing: {selectedCommand}");
                Console.WriteLine();
                
                // Execute the selected command
                return ProcessCommandAsync(selectedCommand);
            }
            else if (!string.IsNullOrEmpty(selection))
            {
                Console.WriteLine("Invalid selection.");
            }
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("Tip: Use 'search -i <term>' for interactive selection and execution.");
        }

        return Task.CompletedTask;
    }

    private Task HelpCommand(string[] args)
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine("Process Management:");
        Console.WriteLine("  attach <pid>              - Attach to running process");
        Console.WriteLine("  spawn <exe> [args]        - Spawn new process");
        Console.WriteLine("  run <exe> [args]          - Spawn process and start profiling");
        Console.WriteLine("  kill                      - Terminate spawned process");
        Console.WriteLine("  start                     - Start memory profiling");
        Console.WriteLine("  stop                      - Stop memory profiling");
        Console.WriteLine("  info                      - Show profiler status and data summary");
        Console.WriteLine("  heap                      - Show heap statistics");
        Console.WriteLine("  allocations [count]       - Show recent allocations");
        Console.WriteLine("  report                    - Generate analysis report");
        Console.WriteLine("  sites                     - Show allocation sites with stack traces");
        Console.WriteLine("  export [filename]         - Export detailed report to JSON");
        Console.WriteLine();
        Console.WriteLine("Heap Analysis:");
        Console.WriteLine("  snapshot                  - Take heap snapshot");
        Console.WriteLine("  snapshots                 - List all available snapshots");
        Console.WriteLine("  switch <id>               - Switch to a specific snapshot");
        Console.WriteLine("  compare <id1> <id2>       - Compare two snapshots");
        Console.WriteLine("  analyze                   - Analyze current snapshot");
        Console.WriteLine("  types [-limit N] [-sort count|size] - List all unique types");
        Console.WriteLine("  dominators [-limit N] [-minsize B]  - Memory dominators by retained size");
        Console.WriteLine("  objects <type> [count]    - Show objects of specified type");
        Console.WriteLine("  objects address <addr>    - Show basic object details");
        Console.WriteLine("  inspect <addr> [-depth N] - Deep object structure inspection");
        Console.WriteLine("  inspect <type> <idx> [-depth N] - Inspect object by type & index");
        Console.WriteLine("  refs <addr> [in|out]      - Show object references");
        Console.WriteLine("  roots <addr>              - Show GC root paths for an object");
        Console.WriteLine("  gcroots [limit]           - Show all GC roots in the heap");
        Console.WriteLine();
        Console.WriteLine("Utility:");
        Console.WriteLine("  search <term>             - Search command history");
        Console.WriteLine("  search -i <term>          - Interactive history search & execute");
        Console.WriteLine("  clear                     - Clear collected data");
        Console.WriteLine("  quit, q                   - Exit profiler");
        Console.WriteLine("  help, h                   - Show this help");
        Console.WriteLine();
        Console.WriteLine("Navigation:");
        Console.WriteLine("  Use ↑↓ arrows to navigate command history");
        Console.WriteLine("  Use Tab for command completion");
        Console.WriteLine("  Use 'search <term>' to find previous commands");
        Console.WriteLine();
        Console.WriteLine("Note: Analysis commands work even after the target process has exited.");
        Console.WriteLine("Addresses can be in hex format (e.g., 1A2B3C4D or 0x1A2B3C4D)");
        
        return Task.CompletedTask;
    }


    private static string SimplifyTypeName(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return "";
        
        // Handle nested classes like "DemoApp.MemoryAllocator+Foo" -> "Foo"
        if (typeName.Contains('+'))
        {
            var plusIndex = typeName.LastIndexOf('+');
            if (plusIndex >= 0 && plusIndex < typeName.Length - 1)
            {
                return typeName.Substring(plusIndex + 1);
            }
        }
        
        // Extract just the class name from full type name like "System.String" -> "String"
        var lastDotIndex = typeName.LastIndexOf('.');
        if (lastDotIndex > 0 && lastDotIndex < typeName.Length - 1)
        {
            return typeName.Substring(lastDotIndex + 1);
        }
        
        return typeName;
    }

    private static string TruncateString(string str, int maxLength)
    {
        if (str.Length <= maxLength) return str;
        return str.Substring(0, maxLength - 3) + "...";
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }

    private static bool TryParseAddress(string input, out ulong address)
    {
        address = 0;
        
        if (string.IsNullOrEmpty(input))
            return false;
            
        // Remove 0x prefix if present
        var addressStr = input.StartsWith("0x", StringComparison.OrdinalIgnoreCase) 
            ? input.Substring(2) 
            : input;
            
        return ulong.TryParse(addressStr, System.Globalization.NumberStyles.HexNumber, null, out address);
    }

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(_historyFile))
            {
                var lines = File.ReadAllLines(_historyFile);
                foreach (var line in lines.TakeLast(1000)) // Keep last 1000 commands
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        ReadLine.AddHistory(line);
                }
            }
        }
        catch (Exception ex)
        {
            // Silently ignore history load errors
        }
    }

    private void SaveToHistory(string command)
    {
        try
        {
            File.AppendAllText(_historyFile, command + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Silently ignore history save errors
        }
    }
}

public class AutoCompletionHandler : IAutoCompleteHandler
{
    private readonly string[] _commands = {
        "attach", "spawn", "run", "kill", "start", "stop", "info", "heap", 
        "allocations", "report", "sites", "export", "snapshot", 
        "analyze", "types", "dominators", "objects", "inspect", "refs", "roots", "gcroots",
        "search", "clear", "quit", "help"
    };

    public char[] Separators { get; set; } = { ' ', '\t' };

    public string[] GetSuggestions(string text, int index)
    {
        if (string.IsNullOrWhiteSpace(text))
            return _commands;

        return _commands.Where(cmd => cmd.StartsWith(text, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
}

