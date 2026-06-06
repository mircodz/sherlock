#!/usr/bin/env python3
"""Turn Sherlock's folded allocation profile into a Graphviz call graph.

Reads the file written by the profiler (``SHERLOCK_PROFILE_OUT``), whose data
lines are ``bytes<TAB>count<TAB>root;...;leaf`` (``#`` lines are comments), and
emits a pprof/Go-style weighted call graph in the DOT language:

  * nodes are methods, sized and coloured by cumulative weight (white -> red),
    labelled with flat (allocated directly here) and cumulative totals,
  * edges are caller -> callee, with pen-width proportional to the weight that
    flows along them,
  * tiny nodes/edges are pruned, like pprof's -nodefraction / -edgefraction.

Examples
--------
    allocgraph.py sherlock-allocations.txt -o alloc.dot
    allocgraph.py sherlock-allocations.txt | dot -Tsvg -o alloc.svg
    cat sherlock-allocations.txt | allocgraph.py --count
"""

from __future__ import annotations

import argparse
import html as html_mod
import json
import math
import sys
from collections import defaultdict
from dataclasses import dataclass, field


@dataclass
class Node:
    flat: int = 0  # weight allocated with this method as the leaf
    cum: int = 0   # weight of every stack passing through this method


@dataclass
class Profile:
    nodes: dict[str, Node] = field(default_factory=lambda: defaultdict(Node))
    edges: dict[tuple[str, str], int] = field(default_factory=lambda: defaultdict(int))
    total: int = 0


def parse(lines, use_count: bool) -> Profile:
    prof = Profile()
    for raw in lines:
        line = raw.rstrip("\n")
        if not line or line.startswith("#"):
            continue
        parts = line.split("\t")
        if len(parts) < 3:
            continue
        try:
            num_bytes = int(parts[0])
            count = int(parts[1])
        except ValueError:
            continue
        weight = count if use_count else num_bytes
        if weight <= 0:
            continue

        frames = [f for f in parts[2].split(";") if f]
        if not frames:
            frames = ["<no managed frame>"]

        prof.total += weight
        # Cumulative: each distinct frame on the path gets the weight once
        # (so simple recursion doesn't multiply-count a node).
        for name in dict.fromkeys(frames):
            prof.nodes[name].cum += weight
        # Flat: the leaf is where the allocation actually happened.
        prof.nodes[frames[-1]].flat += weight
        # Edges: caller -> callee for each adjacent pair (root..leaf).
        for caller, callee in zip(frames, frames[1:]):
            prof.edges[(caller, callee)] += weight
    return prof


def human(n: int) -> str:
    value = float(n)
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if value < 1024 or unit == "TB":
            return f"{int(value)} {unit}" if unit == "B" else f"{value:.1f} {unit}"
        value /= 1024
    return f"{n} B"


def heat(frac: float) -> str:
    """Graphviz H,S,V colour: white (cold) -> red (hot)."""
    sat = 0.05 + 0.85 * min(max(frac, 0.0), 1.0)
    return f"0.000 {sat:.3f} 1.000"


def escape(text: str) -> str:
    return text.replace("\\", "\\\\").replace('"', '\\"')


def emit_dot(prof: Profile, args) -> str:
    total = prof.total or 1
    node_cut = args.nodefraction * total
    edge_cut = args.edgefraction * total

    # Keep nodes above the fraction, then cap to the heaviest `nodecount`.
    kept = [name for name, n in prof.nodes.items() if n.cum >= node_cut]
    kept.sort(key=lambda name: prof.nodes[name].cum, reverse=True)
    kept = kept[: args.nodecount]
    keep = set(kept)

    ids = {name: f"n{i}" for i, name in enumerate(kept)}

    out: list[str] = []
    out.append(f'digraph "{escape(args.title)}" {{')
    out.append('  graph [rankdir=TB, fontname="Helvetica"];')
    out.append('  node [shape=box, style=filled, fontname="Helvetica"];')
    out.append('  edge [fontname="Helvetica"];')

    unit = "allocations" if args.count else human(total)
    out.append(f'  label="{escape(args.title)}\\n{unit} total"; labelloc=t;')

    for name in kept:
        node = prof.nodes[name]
        cum_frac = node.cum / total
        weight = math.sqrt(cum_frac)
        fontsize = round(9 + 22 * weight)
        flat_s = node.flat if args.count else human(node.flat)
        cum_s = node.cum if args.count else human(node.cum)
        label = (
            f"{escape(name)}\\n"
            f"flat {flat_s} ({node.flat / total:.1%})\\n"
            f"cum {cum_s} ({cum_frac:.1%})"
        )
        out.append(
            f'  {ids[name]} [label="{label}", fillcolor="{heat(weight)}", fontsize={fontsize}];'
        )

    for (caller, callee), w in sorted(prof.edges.items(), key=lambda kv: kv[1], reverse=True):
        if w < edge_cut or caller not in keep or callee not in keep:
            continue
        pen = 1 + 5 * math.sqrt(w / total)
        weight_s = w if args.count else human(w)
        out.append(
            f'  {ids[caller]} -> {ids[callee]} [penwidth={pen:.1f}, label="{escape(str(weight_s))}"];'
        )

    out.append("}")
    return "\n".join(out) + "\n"


_HTML_TEMPLATE = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>{title}</title>
<style>
  html, body {{ margin: 0; height: 100%; font-family: Helvetica, Arial, sans-serif; }}
  #graph {{ width: 100vw; height: 100vh; overflow: hidden; }}
  #graph svg {{ width: 100%; height: 100%; }}
  .err {{ padding: 1rem; color: #b00020; font-family: monospace; white-space: pre-wrap; }}
</style>
</head>
<body>
<div id="graph"></div>
<script src="https://unpkg.com/@viz-js/viz@3.2.4/lib/viz-standalone.js"></script>
<script src="https://unpkg.com/svg-pan-zoom@3.6.1/dist/svg-pan-zoom.min.js"></script>
<script>
  const dot = {dot};
  Viz.instance().then(function (viz) {{
    const svg = viz.renderSVGElement(dot);
    document.getElementById("graph").appendChild(svg);
    try {{ svgPanZoom(svg, {{ controlIconsEnabled: true, fit: true, center: true }}); }} catch (e) {{}}
  }}).catch(function (e) {{
    document.getElementById("graph").innerHTML = '<div class="err">' + String(e) + '</div>';
  }});
</script>
</body>
</html>
"""


def html_page(dot: str, title: str) -> str:
    """Wrap DOT in a standalone page that renders it in-browser (Graphviz via wasm)."""
    return _HTML_TEMPLATE.format(title=html_mod.escape(title), dot=json.dumps(dot))


def main() -> int:
    ap = argparse.ArgumentParser(description="Folded allocation profile -> Graphviz call graph.")
    ap.add_argument("input", nargs="?", help="folded TSV (default: stdin)")
    ap.add_argument("-o", "--output", help="write DOT here (default: stdout)")
    ap.add_argument("--html", help="write a standalone HTML page that renders the graph in-browser")
    ap.add_argument("--count", action="store_true", help="weight by allocation count, not bytes")
    ap.add_argument("--nodefraction", type=float, default=0.005, help="drop nodes below this fraction of total")
    ap.add_argument("--edgefraction", type=float, default=0.001, help="drop edges below this fraction of total")
    ap.add_argument("--nodecount", type=int, default=80, help="keep at most this many heaviest nodes")
    ap.add_argument("--title", default="sherlock allocations", help="graph title")
    args = ap.parse_args()

    if args.input and args.input != "-":
        with open(args.input, encoding="utf-8", errors="replace") as f:
            prof = parse(f, args.count)
    else:
        prof = parse(sys.stdin, args.count)

    if prof.total == 0:
        print("allocgraph: no samples found in input", file=sys.stderr)
        return 1

    dot = emit_dot(prof, args)
    wrote = False
    if args.output:
        with open(args.output, "w", encoding="utf-8") as f:
            f.write(dot)
        wrote = True
    if args.html:
        with open(args.html, "w", encoding="utf-8") as f:
            f.write(html_page(dot, args.title))
        wrote = True
    if not wrote:
        sys.stdout.write(dot)
    return 0


if __name__ == "__main__":
    sys.exit(main())
