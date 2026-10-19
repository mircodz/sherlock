using System;
using System.Buffers;
using System.IO;
using System.Threading;
using Sherlock.Core.HeapModel;
using Sherlock.Core.Storage;

namespace Sherlock.Core.Analysis;

/// <summary>
/// Computes and caches dominators over a persisted heap graph. Lengauer–Tarjan semidominators with path compression
/// give each object's semidominator in one backwards pass over its referrers; each immediate dominator is then the
/// nearest common ancestor of the object's DFS parent and its semidominator (semi-NCA). Working arrays, indexed by
/// DFS preorder number, are scratch files beside the result, so memory stays bounded however large the heap.
/// </summary>
public sealed unsafe class DominatorAnalyzer(Snapshot snapshot)
{
    /// <summary>
    /// Dominator columns by graph node: object ids, then the synthetic root at <see cref="HeapGraph.Root"/>.
    /// <see cref="Idom"/> is each node's immediate dominator (the root's is itself, an unreachable object's is -1),
    /// and <see cref="Children"/> lists each node's dominated children from <see cref="ChildOffsets"/>, largest
    /// retained size first.
    /// </summary>
    public readonly record struct DominatorResult(ReadOnlyMemory<int> Idom, ReadOnlyMemory<ulong> Retained, ReadOnlyMemory<long> ChildOffsets, ReadOnlyMemory<int> Children);

    public DominatorTree Build(CancellationToken cancellationToken = default)
    {
        HeapGraph graph = snapshot.GetHeapGraph(cancellationToken);
        string path = Path.Combine(snapshot.CacheDirectory, "dominators.slab");
        return TryLoad(path, graph) ?? Compute(graph, snapshot.GetInbound(cancellationToken), path, cancellationToken);
    }

    /// <summary>Writes the dominator tree of <paramref name="graph"/> to <paramref name="path"/> and opens it.</summary>
    internal static DominatorTree Compute(HeapGraph graph, InboundIndex inbound, string path, CancellationToken cancellationToken)
    {
        Write(graph, inbound, path, cancellationToken);
        return DominatorTreeStore.Load(path, graph) ?? throw new InvalidDataException($"The dominator tree written to '{path}' doesn't match its graph.");
    }

    private static DominatorTree? TryLoad(string path, HeapGraph graph)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return DominatorTreeStore.Load(path, graph);
        }
        catch (InvalidDataException)
        {
            return null; // recomputed over it
        }
    }

    private static void Write(HeapGraph graph, InboundIndex inbound, string path, CancellationToken cancellationToken)
    {
        string scratch = Path.GetDirectoryName(Path.GetFullPath(path))!;
        int nodes = graph.NodeCount;
        using var number = new ScratchArray<int>(scratch, nodes);  // node -> preorder number + 1; 0 when unreachable
        using var vertex = new ScratchArray<int>(scratch, nodes);  // preorder number -> node
        using var parent = new ScratchArray<int>(scratch, nodes);  // preorder number -> its DFS parent's
        int reachable;
        using (var frames = new ScratchArray<Frame>(scratch, nodes))
        {
            reachable = Preorder(graph, number.Pointer, vertex.Pointer, parent.Pointer, frames.Pointer, cancellationToken);
        }

        using var semi = new ScratchArray<int>(scratch, reachable);
        using (var ancestor = new ScratchArray<int>(scratch, reachable))
        using (var label = new ScratchArray<int>(scratch, reachable))
        using (var trail = new ScratchArray<int>(scratch, reachable))
        {
            Semidominators(inbound, reachable, number.Pointer, vertex.Pointer, parent.Pointer, semi.Pointer,
                ancestor.Pointer, label.Pointer, trail.Pointer, cancellationToken);
        }
        using var idom = new ScratchArray<int>(scratch, reachable);
        ImmediateDominators(reachable, parent.Pointer, semi.Pointer, idom.Pointer, cancellationToken);

        using var retained = new ScratchArray<ulong>(scratch, reachable);
        Retained(graph, reachable, vertex.Pointer, idom.Pointer, retained.Pointer, cancellationToken);
        WriteTree(graph, path, scratch, reachable, vertex.Pointer, idom.Pointer, retained.Pointer, cancellationToken);
    }

    // A DFS frame: the node and the next of its edges to follow.
    private record struct Frame(int Node, long Next);

    // Numbers the nodes reachable from the root in DFS preorder, iteratively; returns how many there are.
    private static int Preorder(HeapGraph graph, int* number, int* vertex, int* parent, Frame* frames, CancellationToken cancellationToken)
    {
        ReadOnlySpan<long> rows = graph.Offsets.Span;
        int* edges = graph.Edges.Pin(out MemoryHandle pin);
        using (pin)
        {
            int root = graph.Root;
            number[root] = 1;
            vertex[0] = root;
            frames[0] = new Frame(root, rows[root]);
            int count = 1;
            for (int depth = 1; depth > 0;)
            {
                ref Frame top = ref frames[depth - 1];
                long end = rows[top.Node + 1];
                int next = -1;
                while (top.Next < end)
                {
                    int target = edges[top.Next++];
                    if (number[target] == 0)
                    {
                        next = target;
                        break;
                    }
                }
                if (next < 0)
                {
                    depth--;
                    continue;
                }
                if ((count & 0xffff) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                number[next] = count + 1;
                vertex[count] = next;
                parent[count] = number[top.Node] - 1;
                count++;
                frames[depth++] = new Frame(next, rows[next]);
            }
            return count;
        }
    }

    // Each node's semidominator: the smallest preorder number from which a path reaches it through larger numbers
    // only. Nodes are linked into a forest in decreasing order; label holds the smallest semidominator on each node's
    // compressed path up the forest, so a referrer numbered after the node contributes in near-constant time.
    private static void Semidominators(InboundIndex inbound, int reachable, int* number, int* vertex, int* parent, int* semi,
        int* ancestor, int* label, int* trail, CancellationToken cancellationToken)
    {
        new Span<int>(ancestor, reachable).Fill(-1);
        for (int i = reachable - 1; i > 0; i--)
        {
            if ((i & 0xffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            int s = parent[i];
            foreach (int referrer in inbound.Of(vertex[i]))
            {
                int j = number[referrer] - 1;
                if (j < 0 || j == i)
                {
                    continue; // an unreachable referrer, or the node itself
                }
                int candidate = j < i ? j : Eval(j, ancestor, label, trail);
                if (candidate < s)
                {
                    s = candidate;
                    if (s == 0)
                    {
                        break; // the root: nothing is smaller
                    }
                }
            }
            semi[i] = s;
            label[i] = s;
            ancestor[i] = parent[i];
        }
    }

    // The smallest semidominator on the forest path from v up to, not including, its tree's root; compresses the path.
    private static int Eval(int v, int* ancestor, int* label, int* trail)
    {
        int depth = 0;
        for (int x = v; ancestor[ancestor[x]] >= 0; x = ancestor[x])
        {
            trail[depth++] = x;
        }
        while (depth > 0)
        {
            int x = trail[--depth];
            int up = ancestor[x];
            if (label[up] < label[x])
            {
                label[x] = label[up];
            }
            ancestor[x] = ancestor[up];
        }
        return label[v];
    }

    // The immediate dominator is the nearest common ancestor, in the dominator tree built so far, of the DFS parent and
    // the semidominator: climb from the parent until reaching a number no larger than the semidominator.
    private static void ImmediateDominators(int reachable, int* parent, int* semi, int* idom, CancellationToken cancellationToken)
    {
        idom[0] = 0;
        for (int i = 1; i < reachable; i++)
        {
            if ((i & 0xffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            int d = parent[i];
            while (d > semi[i])
            {
                d = idom[d];
            }
            idom[i] = d;
        }
    }

    // Dominators precede what they dominate in preorder, so one backwards pass adds each node into its dominator.
    private static void Retained(HeapGraph graph, int reachable, int* vertex, int* idom, ulong* retained, CancellationToken cancellationToken)
    {
        ReadOnlySpan<uint> sizes = graph.Sizes.Span;
        for (int i = 1; i < reachable; i++)
        {
            int node = vertex[i];
            retained[i] = sizes[node] != uint.MaxValue ? sizes[node] : graph.SizeOf(node);
        }
        for (int i = reachable - 1; i > 0; i--)
        {
            if ((i & 0xffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            retained[idom[i]] += retained[i];
        }
    }

    // The columns by node id, each node's children listed largest first, written straight into the slab.
    private static void WriteTree(HeapGraph graph, string path, string scratch, int reachable, int* vertex, int* idom, ulong* retained,
        CancellationToken cancellationToken)
    {
        int nodes = graph.NodeCount;
        using var slab = new SlabBuilder(path, sectionCount: 5);
        long metaAt = slab.Place(SectionType.DomMeta, sizeof(Guid), 1);
        long idomAt = slab.Place(SectionType.DomIdom, sizeof(int), nodes);
        long retainedAt = slab.Place(SectionType.DomRetained, sizeof(ulong), nodes);
        long offsetsAt = slab.Place(SectionType.DomChildOffsets, sizeof(long), nodes + 1L);
        long childrenAt = slab.Place(SectionType.DomChildren, sizeof(int), reachable - 1L);
        byte* file = slab.Map();
        *(Guid*)(file + metaAt) = graph.Id;
        int* dominatorOf = (int*)(file + idomAt);
        ulong* retainedOf = (ulong*)(file + retainedAt);
        long* offsets = (long*)(file + offsetsAt);
        int* children = (int*)(file + childrenAt);

        new Span<int>(dominatorOf, nodes).Fill(-1);
        for (int i = 0; i < reachable; i++)
        {
            int node = vertex[i];
            int dominator = vertex[idom[i]];
            dominatorOf[node] = dominator;
            retainedOf[node] = retained[i];
            if (i > 0)
            {
                offsets[dominator + 1]++;
            }
        }
        for (int node = 0; node < nodes; node++)
        {
            offsets[node + 1] += offsets[node];
        }
        // Each node's start is its cursor while placing, ending at the next node's start; shifting restores them.
        for (int i = 1; i < reachable; i++)
        {
            children[offsets[vertex[idom[i]]]++] = vertex[i];
        }
        Buffer.MemoryCopy(offsets, offsets + 1, (long)nodes * sizeof(long), (long)nodes * sizeof(long));
        offsets[0] = 0;

        // Each row is sorted with its retained sizes gathered beside the ids, so comparisons read memory in order.
        long widest = 0;
        for (int node = 0; node < nodes; node++)
        {
            widest = Math.Max(widest, offsets[node + 1] - offsets[node]);
        }
        using var keyed = new ScratchArray<Keyed>(scratch, widest);
        for (int node = 0; node < nodes; node++)
        {
            if ((node & 0xffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            long start = offsets[node];
            int count = (int)(offsets[node + 1] - start);
            if (count < 2)
            {
                continue;
            }
            var row = new Span<Keyed>(keyed.Pointer, count);
            for (int k = 0; k < count; k++)
            {
                int child = children[start + k];
                row[k] = new Keyed(retainedOf[child], child);
            }
            row.Sort();
            for (int k = 0; k < count; k++)
            {
                children[start + k] = row[k].Node;
            }
        }
        slab.Commit(SlabKind.Dominators, DominatorTreeStore.Version);
    }

    // Largest retained size first, then lowest id.
    private readonly record struct Keyed(ulong Retained, int Node) : IComparable<Keyed>
    {
        public int CompareTo(Keyed other) => Retained != other.Retained ? other.Retained.CompareTo(Retained) : Node.CompareTo(other.Node);
    }
}
