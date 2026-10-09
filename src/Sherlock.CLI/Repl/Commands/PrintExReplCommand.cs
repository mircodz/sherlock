using System.Collections.Generic;
using System.Threading;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Analysis;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Prints a depth-limited reference graph with cycle detection, from the same object model as <c>print</c>
/// and the TUI inspector.</summary>
public sealed class PrintExReplCommand : IReplCommand
{
    private const int DefaultDepth = 2;
    private const int MaxChildren = 12;

    public string Name => "printx";
    public IReadOnlyList<string> Aliases => ["px"];
    public string Summary => "Print an object and what it references, as a tree to a depth.";
    public string Usage => "printx <address> [depth]";
    public int MaxArgs => 2;

    public ReplResult Execute(ReplContext context, string[] args)
    {
        ulong address = Args.Address(args, 0, Usage);
        int depth = Args.Count(args, 1, DefaultDepth, Usage, allowZero: true);

        Snapshot snapshot = context.Snapshot;
        ObjectValue root = snapshot.InspectValue(address);
        var tree = new Tree(Label(root)) { Style = new Style(foreground: Theme.MutedColor) };
        var visited = new HashSet<ulong> { address };
        AddChildren(snapshot, tree, root, depth, visited, context.Cancellation);
        context.Console.Write(tree);
        return ReplResult.Success;
    }

    private static void AddChildren(Snapshot snapshot, IHasTreeNodes parent, ObjectValue value, int depth, HashSet<ulong> visited,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        InspectionPage page = snapshot.InspectChildren(value, 0, MaxChildren);
        foreach (ObjectValue child in page.Items)
        {
            string name = Markup.Escape(child.Name);
            if (child.Kind != ObjectValueKind.Reference || child.Address is not { } address)
            {
                parent.AddNode($"{name} [{Palette.Muted}]=[/] {Scalar(child)}");
                continue;
            }
            if (depth <= 0)
            {
                continue; // references appear only while depth remains
            }
            if (!visited.Add(address))
            {
                parent.AddNode($"{name} [{Palette.Muted}]→[/] {Label(child)} [{Palette.Muted}](seen)[/]");
                continue;
            }
            TreeNode node = parent.AddNode($"{name} [{Palette.Muted}]→[/] {Label(child)}");
            AddChildren(snapshot, node, child, depth - 1, visited, cancellation);
        }
        if (page.HasMore)
        {
            parent.AddNode($"[{Palette.Muted}]… {page.TotalCount - page.Items.Count} more (print shows every field)[/]");
        }
    }

    private static string Label(ObjectValue value) =>
        Styled.Object(value.TypeName, value.Address ?? 0, (long)(value.Size ?? 0));

    private static string Scalar(ObjectValue value) => value.Kind switch
    {
        ObjectValueKind.Null => $"[{Palette.Muted}]null[/]",
        ObjectValueKind.String => $"[{Palette.Text}]{Markup.Escape(value.Value)}[/]",
        ObjectValueKind.Unreadable => $"[{Palette.Warning}]{Markup.Escape(value.Value)}[/]",
        _ => Markup.Escape(value.Value),
    };
}
