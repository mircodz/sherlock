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
    public int MaxArgs => 1;

    public ReplResult Execute(ReplContext context, string[] args)
    {
        ulong address = Args.Address(args, 0, Usage);

        context.Console.MarkupLineInterpolated($"[{Palette.Muted}]Searching for roots of[/] [{Palette.Address}]{Addresses.Format(address)}[/][{Palette.Muted}]…[/]");

        IReadOnlyList<GcRootPath> paths = context.Console.Status()
            .Start("Tracing the heap graph…", _ => context.Snapshot.Roots(address, context.Cancellation));

        if (paths.Count == 0)
        {
            context.Console.MarkupLine($"[{Palette.Warning}]No root found.[/] The object may be unrooted (eligible for collection) or the address may be invalid.");
            return ReplResult.Success;
        }

        context.Console.MarkupLineInterpolated($"[{Palette.Muted}]{Counts.Format(paths.Count)} root{(paths.Count == 1 ? "" : "s")} found[/]");
        foreach (GcRootPath path in paths)
        {
            context.Console.MarkupInterpolated($"[bold]{path.Root.Kind}[/] [{Palette.Muted}]at[/] [{Palette.Address}]{Addresses.Format(path.Root.Address)}[/]");
            context.Console.MarkupLine(path.Root.IsPinned ? $" [{Palette.Warning}]pinned[/]" : "");
            for (int i = 0; i < path.Path.Count; i++)
            {
                GcRootNode node = path.Path[i];
                string indent = new string(' ', i * 2);
                context.Console.MarkupLine($"{indent}[{Palette.Muted}]→[/] {Styled.Address(node.Address)} {Styled.Type(node.TypeName)}");
            }
        }
        return ReplResult.Success;
    }
}
