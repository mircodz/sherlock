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
    public int MaxArgs => 1;

    public ReplResult Execute(ReplContext context, string[] args)
    {
        ulong address = Args.Address(args, 0, Usage);

        DominatorTree tree = context.Console.Status()
            .Start("Building dominator tree…", _ => context.Snapshot.GetDominatorTree(context.Cancellation));

        DominatorNode? node = tree.Find(address);
        if (node is null)
        {
            context.Console.MarkupLine($"[{Palette.Warning}]That object is not reachable from any GC root[/] (so its retained size is 0 — it is collectable).");
            return ReplResult.Success;
        }

        context.Console.MarkupLine(Styled.Object(node.TypeName, node.Address, (long)node.OwnSize));
        context.Console.MarkupLine($"  [{Palette.Muted}]retains[/] {Styled.HotSize((long)node.RetainedSize)}");

        IReadOnlyList<DominatorNode> children = tree.ImmediateChildren(address, ChildLimit);
        if (children.Count == 0)
        {
            return ReplResult.Success;
        }

        context.Console.MarkupLine($"[{Palette.Muted}]Directly dominates:[/]");
        foreach (DominatorNode child in children)
        {
            context.Console.MarkupLine($"  {Styled.HotSize((long)child.RetainedSize)}  {Styled.Address(child.Address)}  {Styled.Type(child.TypeName)}");
        }
        return ReplResult.Success;
    }
}
