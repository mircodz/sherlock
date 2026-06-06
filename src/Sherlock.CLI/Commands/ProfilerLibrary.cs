using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Sherlock.CLI.Commands;

/// <summary>
/// Locates the native CLR allocation profiler library produced by
/// <c>src/native/build.sh</c> (or <c>build.cmd</c>).
/// </summary>
public static class ProfilerLibrary
{
    /// <summary>Platform-specific name of the built shared library.</summary>
    public static string FileName =>
        OperatingSystem.IsWindows() ? "SherlockProfiler.dll" :
        OperatingSystem.IsMacOS() ? "libSherlockProfiler.dylib" :
        "libSherlockProfiler.so";

    /// <summary>
    /// Returns the absolute path to the profiler library, or null if it hasn't been
    /// built. Honors a <c>SHERLOCK_PROFILER_PATH</c> override, otherwise searches up
    /// from the executable and working directory for <c>src/native/bin/&lt;lib&gt;</c>.
    /// </summary>
    public static string? Locate()
    {
        string? overridePath = Environment.GetEnvironmentVariable("SHERLOCK_PROFILER_PATH");
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        // Packaged next to the tool: runtimes/<rid>/native/<lib>.
        string bundled = Path.Combine(AppContext.BaseDirectory, "runtimes", Rid(), "native", FileName);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        // Dev tree: built directly under src/native/bin by build.sh / build.cmd.
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (DirectoryInfo? dir = new(start); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "src", "native", "bin", FileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>The runtime identifier of the current process, e.g. osx-arm64, linux-x64, win-x64.</summary>
    public static string Rid()
    {
        string os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : "linux";
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };
        return os + "-" + arch;
    }
}
