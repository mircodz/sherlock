using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Sherlock.CLI.Export;

/// <summary>
/// A weighted directed graph rendered to Graphviz DOT, styled after Go's pprof: nodes are boxes with
/// a light pastel fill and strong-coloured text, both shaded grey->red by their share of the whole;
/// the font grows with a node's own (self) weight; edges are shaded and thickened by the flow along
/// them. The colour ramp mirrors pprof's <c>dotColor</c>. Both the dominator tree and the allocation
/// call graph build through this, so they read identically. Render with <c>dot -Tsvg g.dot -o g.svg</c>.
/// </summary>
public sealed class DotGraph(string name)
{
    private readonly List<Node> _nodes = [];
    private readonly List<Edge> _edges = [];

    private readonly record struct Node(string Id, double Heat, double Size, string[] Lines);
    private readonly record struct Edge(string From, string To, double Heat, string? Label);

    /// <param name="heat">0..1 share of the whole -> colour (grey to red).</param>
    /// <param name="size">0..1 relative self weight -> font size.</param>
    public void AddNode(string id, double heat, double size, params string[] lines) => _nodes.Add(new(id, heat, size, lines));

    /// <param name="heat">0..1 share of the whole -> edge colour and thickness.</param>
    public void AddEdge(string from, string to, double heat, string? label = null) => _edges.Add(new(from, to, heat, label));

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"digraph {name} {{");
        sb.AppendLine("  graph [rankdir=TB, fontname=\"Helvetica\", nodesep=0.2, ranksep=0.5];");
        sb.AppendLine("  node [shape=box, style=filled, fontname=\"Helvetica\", fillcolor=\"#f8f8f8\"];");
        sb.AppendLine("  edge [fontname=\"Helvetica\", fontsize=9];");

        foreach (Node node in _nodes)
        {
            int fontSize = 8 + (int)Math.Ceiling(16.0 * Math.Sqrt(Math.Clamp(node.Size, 0, 1)));
            string label = string.Join("\\n", Array.ConvertAll(node.Lines, line => Escape(Cap(line))));
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {node.Id} [label=\"{label}\", fontsize={fontSize}, fillcolor=\"{Heat(node.Heat, foreground: false)}\", color=\"{Heat(node.Heat, foreground: true)}\", fontcolor=\"{Heat(node.Heat, foreground: true)}\"];");
        }

        foreach (Edge edge in _edges)
        {
            double penWidth = 1 + Math.Min(edge.Heat * 5, 5);
            string color = Heat(edge.Heat, foreground: true);
            string tail = edge.Label is null
                ? string.Create(CultureInfo.InvariantCulture, $"penwidth={penWidth:0.##}, color=\"{color}\"")
                : string.Create(CultureInfo.InvariantCulture, $"label=\"{Escape(edge.Label)}\", penwidth={penWidth:0.##}, color=\"{color}\"");
            sb.AppendLine($"  {edge.From} -> {edge.To} [{tail}];");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// pprof's heat colour: grey at 0, red as the share approaches 1. Foreground (text/border/edge) is
    /// strong and dark; background (fill) is a light pastel of the same hue.
    /// </summary>
    private static string Heat(double score, bool foreground)
    {
        const double shift = 0.7;
        double saturation = foreground ? 1.0 : 0.1;
        double value = foreground ? 0.7 : 0.93;

        score = Math.Clamp(score, -1.0, 1.0);
        if (Math.Abs(score) < 0.2)
        {
            saturation *= Math.Abs(score) / 0.2; // fade to grey near zero
        }
        score = Math.Sign(score) * Math.Pow(Math.Abs(score), 1.0 - shift); // push away from grey

        double blue = value * (1 - saturation);
        double red, green;
        if (score >= 0)
        {
            red = value;
            green = value * (1 - saturation * score);
        }
        else
        {
            green = value;
            red = value * (1 + saturation * score);
        }
        return $"#{Channel(red)}{Channel(green)}{Channel(blue)}";
    }

    private static string Channel(double c) => ((int)Math.Round(Math.Clamp(c, 0, 1) * 255)).ToString("x2", CultureInfo.InvariantCulture);

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Cap(string text) => text.Length <= 46 ? text : text[..46] + "...";
}
