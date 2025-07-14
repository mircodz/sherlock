using System.Diagnostics;

namespace Sherlock.Profiler;

/// <summary>
/// Simple process management for heap analysis. No continuous profiling.
/// </summary>
public class ProcessProfiler : IDisposable
{
    private Process? _targetProcess;
    private bool _disposed;

    public bool IsAttached => _targetProcess?.HasExited == false;
    public int? ProcessId => _targetProcess?.Id;
    public string? ProcessName => _targetProcess?.ProcessName;

    public async Task<bool> AttachToProcessAsync(int processId)
    {
        try
        {
            _targetProcess = Process.GetProcessById(processId);
            Console.WriteLine($"Attached to process {processId}: {_targetProcess.ProcessName}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to attach to process {processId}: {ex.Message}");
            return false;
        }
    }

    public async Task<Process?> SpawnProcessAsync(string executable, string? arguments = null)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments ?? "",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _targetProcess = Process.Start(startInfo);
            if (_targetProcess == null)
                return null;

            // Wait for the process to initialize
            await Task.Delay(1000);

            // Check if process is still running
            if (_targetProcess.HasExited)
            {
                Console.WriteLine($"Process exited quickly with code: {_targetProcess.ExitCode}");
                return null;
            }

            Console.WriteLine($"Spawned process {_targetProcess.Id}: {_targetProcess.ProcessName}");
            return _targetProcess;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to spawn process {executable}: {ex.Message}");
            return null;
        }
    }

    public void KillProcess()
    {
        try
        {
            if (_targetProcess != null && !_targetProcess.HasExited)
            {
                _targetProcess.Kill();
                Console.WriteLine($"Killed process {_targetProcess.Id}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to kill process: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            _targetProcess?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Error disposing profiler: {ex.Message}");
        }
        finally
        {
            _disposed = true;
        }
    }
}