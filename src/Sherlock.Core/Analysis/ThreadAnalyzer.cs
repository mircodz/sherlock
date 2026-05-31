using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>Enumerates managed threads and their call stacks.</summary>
public sealed class ThreadAnalyzer
{
    private readonly DumpSession _session;

    public ThreadAnalyzer(DumpSession session) => _session = session;

    public IReadOnlyList<ThreadInfo> GetThreads(bool includeStacks = true)
    {
        var result = new List<ThreadInfo>();

        foreach (ClrThread thread in _session.Runtime.Threads)
        {
            IReadOnlyList<StackFrameInfo> frames = includeStacks
                ? ReadStack(thread)
                : Array.Empty<StackFrameInfo>();

            result.Add(new ThreadInfo(
                ManagedThreadId: thread.ManagedThreadId,
                OsThreadId: thread.OSThreadId,
                IsAlive: thread.IsAlive,
                IsGcThread: thread.IsGc,
                IsFinalizer: thread.IsFinalizer,
                State: thread.State.ToString(),
                StackTrace: frames));
        }

        return result;
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
