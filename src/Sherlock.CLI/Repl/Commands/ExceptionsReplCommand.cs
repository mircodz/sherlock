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
    public int MaxArgs => 0;

    public ReplResult Execute(ReplContext context, string[] args)
    {
        IReadOnlyList<ExceptionInfo> exceptions = context.Console.Status()
            .Start("Scanning for exceptions…", _ => context.Snapshot.GetExceptions(context.Cancellation));

        if (exceptions.Count == 0)
        {
            context.Console.MarkupLine($"[{Palette.Hot}]No exception objects found.[/]");
            return ReplResult.Success;
        }

        foreach (ExceptionInfo ex in exceptions)
        {
            string thread = ex.ThreadId is int id
                ? $" [{Palette.Warning}](in-flight on thread {id})[/]"
                : "";
            context.Console.MarkupLine($"{Styled.Type(ex.TypeName)} {Styled.Address(ex.Address)}");
            context.Console.MarkupInterpolated($"  {ex.Message ?? "<no message>"}");
            context.Console.MarkupLine(thread);
            if (ex.StackFrameCount > 0)
            {
                context.Console.MarkupLineInterpolated($"  [{Palette.Muted}]{ex.StackFrameCount} stack frames[/]");
            }
        }

        context.Console.MarkupLine($"[{Palette.Muted}]{exceptions.Count} exception object(s).[/]");
        return ReplResult.Success;
    }
}
