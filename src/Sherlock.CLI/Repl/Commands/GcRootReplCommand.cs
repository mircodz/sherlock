using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Finds GC root paths keeping a given object alive.</summary>
public sealed class GcRootReplCommand : IReplCommand
{
    public string Name => "gcroot";
    public string Summary => "Find every GC root that keeps an object alive.";
    public string Usage => "gcroot <address>";

    public void Execute(ReplContext context, string[] args)
    {
        ulong address = Args.Address(args, 0, Usage);

        context.Console.MarkupLineInterpolated($"[#808791]Searching for roots of[/] [#FFD75F]0x{address:x12}[/][#808791]…[/]");

        IReadOnlyList<GcRootPath> paths = context.Console.Status()
            .Start("Tracing the heap graph…", _ => context.Snapshot.Roots(address));

        if (paths.Count == 0)
        {
            context.Console.MarkupLine("[#FFAF00]No root found.[/] The object may be unrooted (eligible for collection) or the address may be invalid.");
            return;
        }

        context.Console.MarkupLineInterpolated($"[#808791]{Counts.Format(paths.Count)} root{(paths.Count == 1 ? "" : "s")} found[/]");
        foreach (GcRootPath path in paths)
        {
            string flags = path.Root.IsPinned ? " [#FFAF00]pinned[/]" : "";
            context.Console.MarkupLineInterpolated($"[bold]{path.Root.Kind}[/] [#808791]at[/] [#FFD75F]0x{path.Root.Address:x12}[/]{flags}");
            for (int i = 0; i < path.Path.Count; i++)
            {
                GcRootNode node = path.Path[i];
                string indent = new string(' ', i * 2);
                context.Console.MarkupLineInterpolated($"{indent}[#808791]->[/] [#FFD75F]0x{node.Address:x12}[/] [#00D7FF]{TypeNames.Short(node.TypeName)}[/]");
            }
        }
    }
}
