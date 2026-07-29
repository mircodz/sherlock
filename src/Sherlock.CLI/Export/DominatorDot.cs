using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core.Analysis;

namespace Sherlock.CLI.Export;

/// <summary>The dominator tree as a DOT retention graph: objects hanging off a synthetic GC-roots node, shaded and sized by the heap share they retain.</summary>
public static class DominatorDot
{
    public static string Write(DominatorGraph graph)
    {
        double total = graph.TotalReachableBytes == 0 ? 1 : graph.TotalReachableBytes;
        double maxRetained = graph.Nodes.Count > 0 ? graph.Nodes.Max(n => n.RetainedSize) : 1;

        var dot = new DotGraph("dominators");
        dot.AddNode("roots", heat: 0, size: 0, "GC roots");

        foreach (DominatorGraphNode node in graph.Nodes)
        {
            double share = node.RetainedSize / total;
            dot.AddNode($"n{node.Id}", heat: share, size: node.RetainedSize / maxRetained,
                TypeNames.Short(node.TypeName),
                $"{ByteSize.Format((long)node.RetainedSize)} ({100 * share:0.0}%)",
                $"0x{node.Address:x}");
        }

        foreach (DominatorGraphNode node in graph.Nodes)
        {
            dot.AddEdge(node.ParentId is int parent ? $"n{parent}" : "roots", $"n{node.Id}", node.RetainedSize / total);
        }

        return dot.Render();
    }
}
