using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Finds a GC root path keeping a given object alive.</summary>
public sealed class GcRootReplCommand : IReplCommand
{
    public string Name => "gcroot";
    public string Summary => "Find a GC root path that keeps an object (by address) alive.";
    public string Usage => "gcroot <address>";

    public void Execute(ReplContext context, string[] args)
    {
        if (args.Length == 0)
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] usage: {Usage}");
            return;
        }

        if (!Addresses.TryParse(args[0], out ulong address))
        {
            context.Console.MarkupLineInterpolated($"[red]error:[/] '{args[0]}' is not a valid object address.");
            return;
        }

        context.Console.MarkupLineInterpolated($"[grey]Searching for roots of[/] 0x{address:x}[grey]…[/]");

        IReadOnlyList<GcRootPath> paths = context.Console.Status()
            .Start("Walking references from GC roots…", _ =>
                new RootAnalyzer(context.Session).FindRoots(address, maxPaths: 1));

        if (paths.Count == 0)
        {
            context.Console.MarkupLine("[yellow]No root found.[/] The object may be unrooted (eligible for collection) or the address may be invalid.");
            return;
        }

        foreach (GcRootPath path in paths)
        {
            context.Console.MarkupLineInterpolated($"[bold]{path.RootDescription}[/]");
            for (int i = 0; i < path.Path.Count; i++)
            {
                GcRootNode node = path.Path[i];
                string indent = new string(' ', i * 2);
                context.Console.MarkupLineInterpolated($"{indent}[grey]->[/] 0x{node.Address:x} [aqua]{node.TypeName}[/]");
            }
        }
    }
}
