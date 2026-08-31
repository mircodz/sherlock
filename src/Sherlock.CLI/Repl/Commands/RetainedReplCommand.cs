using System.Collections.Generic;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Shows an object's retained size (memory freed if it dies) and what it directly dominates.</summary>
public sealed class RetainedReplCommand : IReplCommand
{
    private const int ChildLimit = 15;

    public string Name => "retained";
    public string Summary => "Show an object's retained size and what it dominates.";
    public string Usage => "retained <address>";

    public void Execute(ReplContext context, string[] args)
    {
        ulong address = Args.Address(args, 0, Usage);

        DominatorTree tree = context.Console.Status()
            .Start("Building dominator tree…", _ => context.Snapshot.Dominators);

        DominatorNode? node = tree.Find(address);
        if (node is null)
        {
            context.Console.MarkupLine("[#FFAF00]That object is not reachable from any GC root[/] (so its retained size is 0 — it is collectable).");
            return;
        }

        context.Console.MarkupLineInterpolated($"[bold]{node.TypeName}[/] [#808791]@[/] [#FFD75F]0x{node.Address:x}[/]");
        context.Console.MarkupLineInterpolated($"  [#808791]shallow[/] {ByteSize.Format((long)node.OwnSize)}   [#808791]retained[/] [bold #AFFF00]{ByteSize.Format((long)node.RetainedSize)}[/]");

        IReadOnlyList<DominatorNode> children = tree.ImmediateChildren(address, ChildLimit);
        if (children.Count == 0)
        {
            return;
        }

        context.Console.MarkupLine("[#808791]Directly dominates:[/]");
        foreach (DominatorNode child in children)
        {
            context.Console.MarkupLineInterpolated(
                $"  [bold #AFFF00]{ByteSize.Format((long)child.RetainedSize)}[/]  [#FFD75F]0x{child.Address:x}[/]  [#00D7FF]{TypeNames.Short(child.TypeName)}[/]");
        }
    }
}
