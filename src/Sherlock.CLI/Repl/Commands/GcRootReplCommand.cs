using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Finds GC root paths keeping a given object alive.</summary>
public sealed class GcRootReplCommand : IReplCommand
{
    private const int DefaultPaths = 3;

    public string Name => "gcroot";
    public string Summary => "Find GC root paths that keep an object (by address) alive.";
    public string Usage => "gcroot <address> [max-paths]";

    public void Execute(ReplContext context, string[] args)
    {
        ulong address = Args.Address(args, 0, Usage);
        int maxPaths = Args.Limit(args, 1, DefaultPaths);

        context.Console.MarkupLineInterpolated($"[grey]Searching for roots of[/] 0x{address:x}[grey]…[/]");

        IReadOnlyList<GcRootPath> paths = context.Console.Status()
            .Start("Walking references from GC roots…", _ =>
                context.Snapshot.Roots(address, maxPaths));

        if (paths.Count == 0)
        {
            context.Console.MarkupLine("[yellow]No root found.[/] The object may be unrooted (eligible for collection) or the address may be invalid.");
            return;
        }

        for (int p = 0; p < paths.Count; p++)
        {
            GcRootPath path = paths[p];
            if (paths.Count > 1)
            {
                context.Console.MarkupLineInterpolated($"[grey]— path {p + 1}/{paths.Count} —[/]");
            }
            context.Console.MarkupLineInterpolated($"[bold]{path.RootDescription}[/]");
            for (int i = 0; i < path.Path.Count; i++)
            {
                GcRootNode node = path.Path[i];
                string indent = new string(' ', i * 2);
                context.Console.MarkupLineInterpolated($"{indent}[grey]->[/] 0x{node.Address:x} [aqua]{TypeNames.Short(node.TypeName)}[/]");
            }
        }

        context.Console.MarkupLine($"[grey]{paths.Count} root path(s). More with[/] gcroot <address> <n>[grey].[/]");
    }
}
