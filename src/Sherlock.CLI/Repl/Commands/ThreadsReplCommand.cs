using System.Collections.Generic;
using System.Linq;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// Lists managed threads, or prints the managed call stack of one thread when
/// given a managed thread id.
/// </summary>
public sealed class ThreadsReplCommand : IReplCommand
{
    public string Name => "threads";
    public IReadOnlyList<string> Aliases => ["t", "clrstack"];
    public string Summary => "List managed threads, or show one thread's stack with `threads <id>`.";
    public string Usage => "threads [managed-thread-id]";

    public void Execute(ReplContext context, string[] args)
    {
        var analyzer = new ThreadAnalyzer(context.Session);

        if (args.Length > 0)
        {
            if (!int.TryParse(args[0], out int id))
            {
                context.Console.MarkupLineInterpolated($"[red]error:[/] '{args[0]}' is not a managed thread id.");
                return;
            }

            ThreadInfo? thread = analyzer.GetThreads(includeStacks: true)
                .FirstOrDefault(t => t.ManagedThreadId == id);

            if (thread is null)
            {
                context.Console.MarkupLineInterpolated($"[yellow]No managed thread with id {id}.[/]");
                return;
            }

            PrintStack(context.Console, thread);
            return;
        }

        IReadOnlyList<ThreadInfo> threads = analyzer.GetThreads(includeStacks: false);

        var table = new Table().Border(TableBorder.Rounded);
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
        context.Console.MarkupLine($"[grey]{threads.Count} managed threads. Use[/] threads <id> [grey]for a stack.[/]");
    }

    private static string Flags(ThreadInfo thread)
    {
        var flags = new List<string>();
        if (thread.IsFinalizer)
        {
            flags.Add("[blue]finalizer[/]");
        }

        if (thread.IsGcThread)
        {
            flags.Add("[magenta]gc[/]");
        }

        if (!thread.IsAlive)
        {
            flags.Add("[grey]dead[/]");
        }

        return flags.Count == 0 ? "-" : string.Join(" ", flags);
    }

    private static void PrintStack(IAnsiConsole console, ThreadInfo thread)
    {
        console.MarkupLineInterpolated($"[bold]Thread {thread.ManagedThreadId}[/] (OS 0x{thread.OsThreadId:x})");
        if (thread.StackTrace.Count == 0)
        {
            console.MarkupLine("[grey]  <no managed frames>[/]");
            return;
        }

        foreach (StackFrameInfo frame in thread.StackTrace)
            console.MarkupLineInterpolated($"  [grey]{frame.InstructionPointer:x12}[/]  {frame.Description}");
    }
}
