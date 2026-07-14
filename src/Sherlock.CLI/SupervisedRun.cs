using System;
using System.IO;
using System.Threading;
using Sherlock.Core.Collection;
using Sherlock.Core.Store;
using Spectre.Console;

namespace Sherlock.CLI;

/// <summary>
/// Runs a supervised target to completion: streams its output, drains the run-target pollers so
/// triggered snapshots fire and exit-time artifacts (crash dumps, allocation profiles) are captured,
/// and waits a beat after exit for those to flush. Shared by <c>sl run</c> and <c>sl test</c>.
/// </summary>
public static class SupervisedRun
{
    /// <param name="waitForArtifacts">
    /// Poll a beat after exit for crash dumps / allocation profiles to flush. Set when a profiler was
    /// attached; a non-zero exit also triggers it (a crash dump may be landing).
    /// </param>
    public static void Drain(Workspace workspace, IAnsiConsole console, ProcessSupervisor supervisor, CancellationToken cancellation, bool waitForArtifacts)
    {
        long logPos = 0;
        while (!supervisor.RootExited && !cancellation.IsCancellationRequested)
        {
            logPos = StreamLog(supervisor, logPos);
            PumpCaptures(workspace, console);
            Thread.Sleep(120);
        }

        if (cancellation.IsCancellationRequested)
        {
            supervisor.Kill();
            console.MarkupLine("[grey](interrupted)[/]");
        }

        logPos = StreamLog(supervisor, logPos);

        // Exit-time artifacts (crash dump, allocation profile) take a moment to flush after exit.
        if (waitForArtifacts || supervisor.RootExitCode is int code && code != 0)
        {
            for (int i = 0; i < 20 && !cancellation.IsCancellationRequested; i++)
            {
                PumpCaptures(workspace, console);
                Thread.Sleep(150);
            }
        }
        StreamLog(supervisor, logPos);
    }

    /// <summary>Drains the pollers, announcing anything captured.</summary>
    private static void PumpCaptures(Workspace workspace, IAnsiConsole console)
    {
        foreach (SnapshotEntry entry in workspace.PollExitedCrashDumps())
        {
            console.MarkupLineInterpolated($"[yellow]· crash dump[/] [bold]{entry.Id}[/] [grey]captured[/]");
        }
        foreach (Session session in workspace.PollExitedAllocationProfiles())
        {
            console.MarkupLineInterpolated($"[yellow]· allocation profile captured for[/] [bold]{session.Id}[/]");
        }
        foreach ((SnapshotEntry entry, string probe) in workspace.PollProbeSnapshots())
        {
            console.MarkupLineInterpolated($"[yellow]●[/] [bold]{probe}[/] [yellow]fired → snapshot[/] [bold]{entry.Id}[/]");
        }
    }

    /// <summary>Writes any log content past <paramref name="pos"/> straight to stdout, returning the new position.</summary>
    private static long StreamLog(ProcessSupervisor supervisor, long pos)
    {
        string? path = supervisor.LogPath;
        if (path is null || !File.Exists(path))
        {
            return pos;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= pos)
            {
                return pos;
            }
            stream.Seek(pos, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            Console.Out.Write(reader.ReadToEnd());
            Console.Out.Flush();
            return stream.Length;
        }
        catch
        {
            return pos;
        }
    }
}
