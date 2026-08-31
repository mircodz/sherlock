using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Sherlock.Core.HeapModel;

namespace Sherlock.Core.Analysis;

internal static class RootAnalyzer
{
    public static IReadOnlyList<GcRootPath> Find(HeapGraph graph, ulong address, CancellationToken cancellationToken = default)
    {
        int target = graph.IndexOf(address);
        if (target < 0)
        {
            return [];
        }

        var search = new Search(graph, target, cancellationToken);
        var results = new List<GcRootPath>();
        foreach (HeapRootRecord root in graph.Roots.Span)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Link? link = search.FindPathFrom(root.ObjectId);
            if (link is null)
            {
                continue;
            }

            var path = new List<GcRootNode>();
            for (; link is not null; link = link.Next)
            {
                path.Add(new GcRootNode(graph.Addresses.Span[link.Object], graph.TypeNameOf(link.Object) ?? "<unknown>"));
            }
            string kind = root.Kind == Microsoft.Diagnostics.Runtime.ClrRootKind.None ? "GC root" : root.Kind.ToString();
            results.Add(new GcRootPath(new GcRootInfo(root.Address, kind, root.IsInterior, root.IsPinned), path));
        }
        return results;
    }

    private sealed class Search(HeapGraph graph, int target, CancellationToken cancellationToken)
    {
        private readonly uint[] _seen = new uint[checked((int)((graph.ObjectCount + 31L) / 32))];
        private readonly Dictionary<int, Link> _found = new() { [target] = new Link(target, null) };

        public Link? FindPathFrom(int start)
        {
            if (_found.TryGetValue(start, out Link? found))
            {
                return found;
            }

            var stack = new List<References>();
            try
            {
                found = WalkObject(stack, -1, start);
                if (found is not null)
                {
                    DrainUnwalked(stack);
                    return found;
                }

                while (stack.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    References current = stack[^1];
                    int next = current.Next();
                    if (next < 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                        current.Dispose();
                        continue;
                    }

                    found = WalkObject(stack, current.Object, next);
                    if (found is not null)
                    {
                        return CompletePath(stack, found, current);
                    }
                }
                return null;
            }
            finally
            {
                foreach (References references in stack)
                {
                    references.Dispose();
                }
            }
        }

        private Link? WalkObject(List<References> stack, int parent, int current)
        {
            References? references = null;
            ReadOnlySpan<int> successors = graph.Successors(current);
            foreach (int next in successors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_found.TryGetValue(next, out Link? found))
                {
                    var result = new Link(current, found);
                    _found[current] = result;
                    if (references is not null)
                    {
                        stack.Add(references);
                    }
                    return result;
                }
                if (MarkSeen(next))
                {
                    references ??= new References(current, parent, successors.Length);
                    references.Add(next);
                }
            }
            if (references is not null)
            {
                stack.Add(references);
            }
            return null;
        }

        private Link CompletePath(List<References> stack, Link link, References current)
        {
            stack.Reverse();
            int node = current.Object;
            int parent = current.Parent;
            link = AddLink(link, node);
            foreach (References references in stack)
            {
                if (references.Object == node)
                {
                    continue;
                }
                node = references.Object;
                if (node == parent)
                {
                    link = AddLink(link, node);
                    parent = references.Parent;
                }
                else
                {
                    ClearSeen(node);
                }
            }
            DrainUnwalked(stack);
            return link;
        }

        private Link AddLink(Link next, int node)
        {
            if (!_found.TryGetValue(node, out Link? link))
            {
                link = new Link(node, next);
                _found[node] = link;
            }
            ClearSeen(node);
            return link;
        }

        private void DrainUnwalked(List<References> stack)
        {
            foreach (References references in stack)
            {
                int node;
                while ((node = references.Next()) >= 0)
                {
                    ClearSeen(node);
                }
                references.Dispose();
            }
            stack.Clear();
        }

        private bool MarkSeen(int node)
        {
            ref uint word = ref _seen[node >> 5];
            uint mask = 1u << (node & 31);
            if ((word & mask) != 0)
            {
                return false;
            }
            word |= mask;
            return true;
        }

        private void ClearSeen(int node) => _seen[node >> 5] &= ~(1u << (node & 31));
    }

    private sealed class References(int @object, int parent, int capacity) : IDisposable
    {
        private int[]? _items = ArrayPool<int>.Shared.Rent(Math.Max(1, capacity));
        private int _count;
        private int _read;

        public int Object { get; } = @object;
        public int Parent { get; } = parent;

        public void Add(int value) => _items![_count++] = value;
        public int Next() => _read < _count ? _items![_read++] : -1;

        public void Dispose()
        {
            if (_items is null)
            {
                return;
            }
            ArrayPool<int>.Shared.Return(_items);
            _items = null;
        }
    }

    private sealed record Link(int Object, Link? Next);
}
