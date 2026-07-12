using Sherlock.CLI.Rendering;
using Sherlock.Core.Analysis;

namespace Sherlock.CLI.Export;

/// <summary>The dominator tree as a DOT retention graph: objects hanging off a synthetic GC-roots node, coloured and sized by the share of the heap they retain.</summary>
public static class DominatorDot
{
    public static string Write(DominatorGraph graph)
    {
        double total = graph.TotalReachableBytes == 0 ? 1 : graph.TotalReachableBytes;

        var dot = new DotGraph("dominators");
        dot.AddNode("roots", 0, "GC roots");

        foreach (DominatorGraphNode node in graph.Nodes)
        {
            double weight = node.RetainedSize / total;
            dot.AddNode($"n{node.Id}", weight,
                TypeNames.Short(node.TypeName),
                $"{ByteSize.Format((long)node.RetainedSize)} ({100 * weight:0.0}%)",
                $"0x{node.Address:x}");
        }

        foreach (DominatorGraphNode node in graph.Nodes)
        {
            dot.AddEdge(node.ParentId is int parent ? $"n{parent}" : "roots", $"n{node.Id}", node.RetainedSize / total);
        }

        return dot.Render();
    }
}
