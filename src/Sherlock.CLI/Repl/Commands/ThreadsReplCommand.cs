using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Lists managed threads, or prints one thread's managed call stack given its id.</summary>
public sealed class ThreadsReplCommand : IReplCommand
{
    public string Name => "threads";
    public IReadOnlyList<string> Aliases => ["t"];
    public string Summary => "List managed threads, or show one thread's stack with `threads <id>`.";
    public string Usage => "threads [managed-thread-id]";

    public void Execute(ReplContext context, string[] args)
    {
        if (args.Length > 0)
        {
            if (!int.TryParse(args[0], out int id))
            {
                Output.Error(context.Console, $"'{args[0]}' is not a managed thread id.");
                return;
            }

            ThreadInfo? thread = context.Snapshot.Threads.FirstOrDefault(t => t.ManagedThreadId == id);

            if (thread is null)
            {
                context.Console.MarkupLineInterpolated($"[#FFAF00]No managed thread with id {id}.[/]");
                return;
            }

            PrintStack(context.Console, thread);
            return;
        }

        IReadOnlyList<ThreadInfo> threads = context.Snapshot.Threads;

        var table = Theme.Table();
        table.AddColumn(new TableColumn("[bold]Managed[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]OS[/]").RightAligned());
        table.AddColumn("[bold]Flags[/]");
        table.AddColumn("[bold]State[/]");

        foreach (ThreadInfo thread in threads)
        {
            table.AddRow(
                thread.ManagedThreadId.ToString(),
                thread.OsThreadId == 0 ? "-" : $"0x{thread.OsThreadId:x}",
                Flags(thread),
                Markup.Escape(thread.State ?? "-"));
        }

        context.Console.Write(table);
        context.Console.MarkupLine($"[#808791]{threads.Count} managed threads. Use[/] threads <id> [#808791]for a stack.[/]");
    }

    private static string Flags(ThreadInfo thread)
    {
        var flags = new List<string>();
        if (thread.IsFinalizer)
        {
            flags.Add("[#00D7FF]finalizer[/]");
        }

        if (thread.IsGcThread)
        {
            flags.Add("[#00D7FF]gc[/]");
        }

        if (!thread.IsAlive)
        {
            flags.Add("[#808791]dead[/]");
        }

        return flags.Count == 0 ? "-" : string.Join(" ", flags);
    }

    private static void PrintStack(IAnsiConsole console, ThreadInfo thread)
    {
        console.MarkupLineInterpolated($"[bold]Thread {thread.ManagedThreadId}[/] (OS 0x{thread.OsThreadId:x})");
        if (thread.StackTrace.Count == 0)
        {
            console.MarkupLine("[#808791]  <no managed frames>[/]");
            return;
        }

        foreach (StackFrameInfo frame in thread.StackTrace)
        {
            console.MarkupLineInterpolated($"  [#808791]{frame.InstructionPointer:x12}[/]  {frame.Description}");
        }
    }
}
