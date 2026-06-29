using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// Shows the retained size of one object and what it directly dominates — i.e.
/// how much memory frees if this object dies, and where that memory lives.
/// </summary>
public sealed class RetainedReplCommand : IReplCommand
{
    private const int ChildLimit = 15;

    public string Name => "retained";
    public IReadOnlyList<string> Aliases => ["objsize"];
    public string Summary => "Show an object's retained size and what it dominates.";
    public string Usage => "retained <address>";

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

        DominatorTree tree = context.Console.Status()
            .Start("Building dominator tree…", _ => context.Session.GetDominatorTree());

        DominatorNode? node = tree.Find(address);
        if (node is null)
        {
            context.Console.MarkupLine("[yellow]That object is not reachable from any GC root[/] (so its retained size is 0 — it is collectable).");
            return;
        }

        context.Console.MarkupLineInterpolated($"[bold]{node.TypeName}[/] [grey]@ 0x{node.Address:x}[/]");
        context.Console.MarkupLineInterpolated($"  [grey]shallow[/] {ByteSize.Format((long)node.OwnSize)}   [grey]retained[/] [bold]{ByteSize.Format((long)node.RetainedSize)}[/]");

        IReadOnlyList<DominatorNode> children = tree.ImmediateChildren(address, ChildLimit);
        if (children.Count == 0)
        {
            return;
        }

        context.Console.MarkupLine("[grey]Directly dominates:[/]");
        foreach (DominatorNode child in children)
        {
            context.Console.MarkupLineInterpolated(
                $"  [bold]{ByteSize.Format((long)child.RetainedSize)}[/]  [grey]0x{child.Address:x}[/]  [aqua]{child.TypeName}[/]");
        }
    }
}
