using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Sherlock.CLI.Export;

/// <summary>
/// A weighted directed graph rendered to Graphviz DOT in one house style: every node is a rounded
/// box coloured (red heat) and sized by its weight (0..1), every edge thickened by its weight. Both
/// the dominator tree and the allocation call graph are built through this, so they look identical.
/// Render with <c>dot -Tsvg graph.dot -o graph.svg</c>.
/// </summary>
public sealed class DotGraph(string name)
{
    private readonly List<(string Id, double Weight, string[] Lines)> _nodes = [];
    private readonly List<(string From, string To, double Weight, string? Label)> _edges = [];

    /// <param name="weight">0..1 share of the whole; drives colour and size.</param>
    public void AddNode(string id, double weight, params string[] lines) => _nodes.Add((id, weight, lines));

    public void AddEdge(string from, string to, double weight, string? label = null) => _edges.Add((from, to, weight, label));

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"digraph {name} {{");
        sb.AppendLine("  graph [rankdir=TB, fontname=\"Helvetica\"];");
        sb.AppendLine("  node [shape=box, style=\"filled,rounded\", fontname=\"Helvetica\", color=\"#00000022\"];");
        sb.AppendLine("  edge [color=\"#888888\", fontname=\"Helvetica\", fontsize=9];");

        foreach ((string id, double weight, string[] lines) in _nodes)
        {
            double saturation = 0.12 + 0.88 * weight;   // faint tint -> deep red
            int fontSize = (int)(11 + 24 * weight);      // small -> large
            string label = string.Join("\\n", System.Array.ConvertAll(lines, l => Escape(Cap(l))));
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {id} [label=\"{label}\", fillcolor=\"0.0 {saturation:0.###} 1.0\", fontsize={fontSize}];");
        }

        foreach ((string from, string to, double weight, string? label) in _edges)
        {
            double pen = 1 + 6 * weight;
            string attrs = label is null
                ? string.Create(CultureInfo.InvariantCulture, $"penwidth={pen:0.##}")
                : string.Create(CultureInfo.InvariantCulture, $"label=\"{Escape(label)}\", penwidth={pen:0.##}");
            sb.AppendLine($"  {from} -> {to} [{attrs}];");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Escapes what matters inside a quoted DOT label; intentional <c>\n</c> breaks are added separately.</summary>
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Keeps labels from blowing up the box.</summary>
    private static string Cap(string text) => text.Length <= 46 ? text : text[..46] + "...";
}
