using System.Collections.Generic;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Lists managed exception objects (on threads and on the heap).</summary>
public sealed class ExceptionsReplCommand : IReplCommand
{
    public string Name => "exceptions";
    public IReadOnlyList<string> Aliases => new[] { "pe", "exc" };
    public string Summary => "List managed exceptions on threads and on the heap.";
    public string Usage => "exceptions";

    public void Execute(ReplContext context, string[] args)
    {
        IReadOnlyList<ExceptionInfo> exceptions = context.Console.Status()
            .Start("Scanning for exceptions…", _ => new ExceptionAnalyzer(context.Session).FindExceptions());

        if (exceptions.Count == 0)
        {
            context.Console.MarkupLine("[green]No exception objects found.[/]");
            return;
        }

        foreach (ExceptionInfo ex in exceptions)
        {
            string thread = ex.ThreadId is int id
                ? $" [yellow](in-flight on thread {id})[/]"
                : "";
            context.Console.MarkupLineInterpolated($"[red]{ex.TypeName}[/] [grey]@ 0x{ex.Address:x}[/]");
            context.Console.MarkupInterpolated($"  {ex.Message ?? "<no message>"}");
            context.Console.MarkupLine(thread);
            if (ex.StackFrameCount > 0)
            {
                context.Console.MarkupLineInterpolated($"  [grey]{ex.StackFrameCount} stack frames[/]");
            }
        }

        context.Console.MarkupLine($"[grey]{exceptions.Count} exception object(s).[/]");
    }
}
