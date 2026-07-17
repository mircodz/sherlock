using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Diagnostics.Runtime;

namespace Sherlock.Core.Analysis;

/// <summary>
/// Builds a <see cref="DominatorTree"/> for the managed heap using the Cooper-Harvey-Kennedy iterative
/// dominators algorithm (<i>A Simple, Fast Dominance Algorithm</i>, 2001).
/// </summary>
/// <remarks>
/// The graph is stored in CSR form (flat offset/edge arrays, not a <see cref="List{T}"/> per object)
/// with a sorted-address index (binary search, not a <see cref="Dictionary{TKey,TValue}"/>), which
/// roughly a third faster and far lighter than the naive representation, and scales to large heaps.
/// Extraction stays single-threaded on purpose: ClrMD's DAC (mscordaccore) takes a giant lock around
/// heap reads, so parallelizing across threads/runtimes yields no real speedup (measured, and per the
/// ClrMD 2.0 release notes). Going faster than this needs a lower-level heap reader that bypasses the
/// DAC — a separate effort.
/// </remarks>
public sealed class DominatorAnalyzer(DumpSession session)
{
    public DominatorTree Build(CancellationToken cancellationToken = default)
    {
        ClrHeap heap = session.Runtime.Heap;

        // Enumerate segments in address order so the collected object addresses come out globally
        // sorted — then an address resolves to its dense id by a plain binary search (no Dictionary).
        MemoryRange[] segments = heap.Segments
            .Select(s => s.ObjectRange)
            .Where(r => r.Length > 0)
            .OrderBy(r => r.Start)
            .ToArray();

        // 1. Index every (non-free) object: dense id (address order) + shallow size.
        var addrList = new List<ulong>();
        var sizeList = new List<ulong>();
        foreach (MemoryRange range in segments)
        {
            foreach (ClrObject obj in heap.EnumerateObjects(range))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (obj.Type is null || obj.IsFree)
                {
                    continue;
                }
                addrList.Add(obj.Address);
                sizeList.Add(obj.Size);
            }
        }

        int objectCount = addrList.Count;
        int root = objectCount;          // synthetic root id
        int nodeCount = objectCount + 1;

        ulong[] addresses = addrList.ToArray();  // sorted
        ulong[] sizes = sizeList.ToArray();

        int IndexOf(ulong a)
        {
            int i = Array.BinarySearch(addresses, a);
            return i >= 0 ? i : -1;
        }

        // 2. Successor edges in CSR form: offsets[node]..offsets[node+1] slices into edges. Objects are
        //    re-enumerated in the same order, so the sequential id matches pass 1. The synthetic root is
        //    node `root`, appended last.
        var offsets = new int[nodeCount + 1];
        var edges = new List<int>();
        int id = 0;
        foreach (MemoryRange range in segments)
        {
            foreach (ClrObject obj in heap.EnumerateObjects(range))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (obj.Type is null || obj.IsFree)
                {
                    continue;
                }
                offsets[id] = edges.Count;
                foreach (ClrObject reference in obj.EnumerateReferences())
                {
                    int v = IndexOf(reference.Address);
                    if (v >= 0)
                    {
                        edges.Add(v);
                    }
                }
                id++;
            }
        }

        // Synthetic root -> every GC-rooted object.
        offsets[root] = edges.Count;
        var seenRoot = new HashSet<int>();
        foreach (ClrRoot clrRoot in heap.EnumerateRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            int v = IndexOf(clrRoot.Object.Address);
            if (v >= 0 && seenRoot.Add(v))
            {
                edges.Add(v);
            }
        }
        offsets[nodeCount] = edges.Count;

        ReadOnlySpan<int> Successors(int node) =>
            CollectionsMarshal.AsSpan(edges).Slice(offsets[node], offsets[node + 1] - offsets[node]);

        // 3. Reverse-postorder numbering from the synthetic root (iterative DFS).
        int[] rpoNumber = BuildReversePostorder(Successors, root, nodeCount, out int[] nodeByRpo);
        int m = nodeByRpo.Length;

        // 4. Predecessor lists in RPO space.
        var preds = new List<int>[m];
        for (int i = 0; i < m; i++)
            preds[i] = [];
        for (int node = 0; node < nodeCount; node++)
        {
            int uRpo = rpoNumber[node];
            if (uRpo < 0)
            {
                continue;
            }

            foreach (int v in Successors(node))
            {
                int vRpo = rpoNumber[v];
                if (vRpo >= 0)
                {
                    preds[vRpo].Add(uRpo);
                }
            }
        }

        // 5. Iterative dominators (CHK). idom indexed in RPO space; root dominates itself.
        var idom = new int[m];
        Array.Fill(idom, -1);
        idom[0] = 0;
        bool changed = true;
        while (changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changed = false;
            for (int b = 1; b < m; b++)
            {
                int newIdom = -1;
                foreach (int p in preds[b])
                {
                    if (idom[p] == -1)
                    {
                        continue;
                    }

                    newIdom = newIdom == -1 ? p : Intersect(p, newIdom, idom);
                }
                if (newIdom != -1 && idom[b] != newIdom)
                {
                    idom[b] = newIdom;
                    changed = true;
                }
            }
        }

        // 6. Retained sizes. Walk RPO high->low, accumulating each node into its immediate dominator
        //    (descendants, at higher RPO, are already summed).
        var address = new ulong[m];
        var own = new ulong[m];
        for (int rpo = 0; rpo < m; rpo++)
        {
            int node = nodeByRpo[rpo];
            if (node != root)
            {
                address[rpo] = addresses[node];
                own[rpo] = sizes[node];
            }
        }

        var retained = (ulong[])own.Clone();
        for (int rpo = m - 1; rpo >= 1; rpo--)
            retained[idom[rpo]] += retained[rpo];

        var rpoOf = new Dictionary<ulong, int>(m);
        for (int rpo = 1; rpo < m; rpo++)
            rpoOf[address[rpo]] = rpo;

        return new DominatorTree(heap, address, own, retained, idom, rpoOf);
    }

    /// <summary>
    /// Assigns reverse-postorder numbers (root = 0) to nodes reachable from <paramref name="root"/>.
    /// Unreachable nodes get -1. Iterative to avoid deep recursion.
    /// </summary>
    private static int[] BuildReversePostorder(SuccessorFn successors, int root, int nodeCount, out int[] nodeByRpo)
    {
        var rpoNumber = new int[nodeCount];
        Array.Fill(rpoNumber, -1);

        var postorder = new List<int>();
        var visited = new bool[nodeCount];
        var stack = new Stack<(int Node, int Index)>();

        visited[root] = true;
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            (int node, int index) = stack.Pop();
            ReadOnlySpan<int> succ = successors(node);
            if (index < succ.Length)
            {
                stack.Push((node, index + 1));
                int w = succ[index];
                if (!visited[w])
                {
                    visited[w] = true;
                    stack.Push((w, 0));
                }
            }
            else
            {
                postorder.Add(node);
            }
        }

        int m = postorder.Count;
        nodeByRpo = new int[m];
        for (int k = 0; k < m; k++)
        {
            int node = postorder[m - 1 - k];
            rpoNumber[node] = k;
            nodeByRpo[k] = node;
        }
        return rpoNumber;
    }

    private delegate ReadOnlySpan<int> SuccessorFn(int node);

    /// <summary>Finds the nearest common dominator of two nodes in RPO space.</summary>
    private static int Intersect(int a, int b, int[] idom)
    {
        while (a != b)
        {
            while (a > b)
            {
                a = idom[a];
            }

            while (b > a)
            {
                b = idom[b];
            }
        }
        return a;
    }
}
