using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Lists managed exception objects (on threads and on the heap).</summary>
public sealed class ExceptionsReplCommand : IReplCommand
{
    public string Name => "exceptions";
    public IReadOnlyList<string> Aliases => ["pe", "exc"];
    public string Summary => "List managed exceptions on threads and on the heap.";
    public string Usage => "exceptions";

    public void Execute(ReplContext context, string[] args)
    {
        IReadOnlyList<ExceptionInfo> exceptions = context.Console.Status()
            .Start("Scanning for exceptions…", _ => context.Snapshot.Exceptions);

        if (exceptions.Count == 0)
        {
            context.Console.MarkupLine("[#AFFF00]No exception objects found.[/]");
            return;
        }

        foreach (ExceptionInfo ex in exceptions)
        {
            string thread = ex.ThreadId is int id
                ? $" [#FFAF00](in-flight on thread {id})[/]"
                : "";
            context.Console.MarkupLineInterpolated($"[#00D7FF]{TypeNames.Short(ex.TypeName)}[/] [#808791]@[/] [#FFD75F]0x{ex.Address:x}[/]");
            context.Console.MarkupInterpolated($"  {ex.Message ?? "<no message>"}");
            context.Console.MarkupLine(thread);
            if (ex.StackFrameCount > 0)
            {
                context.Console.MarkupLineInterpolated($"  [#808791]{ex.StackFrameCount} stack frames[/]");
            }
        }

        context.Console.MarkupLine($"[#808791]{exceptions.Count} exception object(s).[/]");
    }
}
