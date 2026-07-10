using System;
using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>Enumerates managed threads and their call stacks.</summary>
public sealed class ThreadAnalyzer(DumpSession session)
{
    public IReadOnlyList<ThreadInfo> GetThreads(bool includeStacks = true)
    {
        var result = new List<ThreadInfo>();

        foreach (ClrThread thread in session.Runtime.Threads)
        {
            var frames = includeStacks
                ? ReadStack(thread)
                : [];

            result.Add(new ThreadInfo(
                ManagedThreadId: thread.ManagedThreadId,
                OsThreadId: thread.OSThreadId,
                IsAlive: thread.IsAlive,
                IsGcThread: thread.IsGc,
                IsFinalizer: thread.IsFinalizer,
                State: FormatState(thread.State),
                StackTrace: frames));
        }

        return result;
    }

    /// <summary>
    /// Decodes the CLR thread-state flags into readable names (the raw value is an opaque
    /// bitmask). Only single-bit named flags are shown; falls back to hex if none are named.
    /// </summary>
    private static string FormatState(ClrThreadState state)
    {
        long bits = Convert.ToInt64(state);
        if (bits == 0)
        {
            return "-";
        }

        var names = new List<string>();
        foreach (ClrThreadState flag in Enum.GetValues<ClrThreadState>())
        {
            long f = Convert.ToInt64(flag);
            if (f != 0 && (f & (f - 1)) == 0 && (bits & f) == f) // single set bit, present
            {
                names.Add(flag.ToString().Replace("TS_", ""));
            }
        }
        
        return names.Count == 0 ? $"0x{bits:x}" : string.Join(",", names);
    }

    private static IReadOnlyList<StackFrameInfo> ReadStack(ClrThread thread)
    {
        var frames = new List<StackFrameInfo>();
        foreach (ClrStackFrame frame in thread.EnumerateStackTrace())
        {
            string description = frame.Method?.Signature
                ?? frame.FrameName
                ?? "<unknown>";
            frames.Add(new StackFrameInfo(frame.InstructionPointer, description));
        }
        
        return frames;
    }
}
