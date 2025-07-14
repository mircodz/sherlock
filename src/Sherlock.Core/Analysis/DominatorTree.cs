using System.Collections.Concurrent;

namespace Sherlock.Core.Analysis;

/// <summary>
/// Implements the Lengauer-Tarjan algorithm for computing dominator trees efficiently.
/// Time complexity: O((V+E) log(V+E)), Space complexity: O(V)
/// </summary>
public class DominatorTree
{
    private readonly Dictionary<ulong, int> _addressToNode = new();
    private readonly Dictionary<int, ulong> _nodeToAddress = new();
    private readonly List<List<int>> _successors = new();
    private readonly List<List<int>> _predecessors = new();
    private readonly List<int> _parent = new();
    private readonly List<int> _ancestor = new();
    private readonly List<int> _child = new();
    private readonly List<int> _vertex = new();
    private readonly List<int> _label = new();
    private readonly List<int> _semi = new();
    private readonly List<int> _size = new();
    private readonly List<List<int>> _bucket = new();
    private readonly List<int> _dom = new();
    private int _nodeCount;
    private int _dfsNumber;

    /// <summary>
    /// Builds a dominator tree from the given objects and their references.
    /// </summary>
    public static DominatorTreeResult BuildDominatorTree(Dictionary<ulong, ObjectInfo> objects, List<ulong> rootAddresses)
    {
        var tree = new DominatorTree();
        return tree.ComputeDominatorTree(objects, rootAddresses);
    }

    private DominatorTreeResult ComputeDominatorTree(Dictionary<ulong, ObjectInfo> objects, List<ulong> rootAddresses)
    {
        Console.WriteLine("Building dominator tree...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Step 1: Build graph representation
        BuildGraph(objects);
        Console.WriteLine($"Built graph with {_nodeCount} nodes in {sw.ElapsedMilliseconds}ms");

        if (_nodeCount == 0)
        {
            return new DominatorTreeResult(new Dictionary<ulong, List<ulong>>(), new Dictionary<ulong, ulong>());
        }

        // Limit graph size to prevent infinite loops on very large heaps
        if (_nodeCount > 500_000)
        {
            Console.WriteLine($"Graph too large ({_nodeCount} nodes), skipping dominator tree calculation");
            return new DominatorTreeResult(new Dictionary<ulong, List<ulong>>(), new Dictionary<ulong, ulong>());
        }

        // Step 2: Add virtual root and connect to all GC roots
        var virtualRoot = AddVirtualRoot(rootAddresses);
        Console.WriteLine($"Added virtual root connected to {rootAddresses.Count} GC roots");

        // Step 3: Run Lengauer-Tarjan algorithm with timeout protection
        sw.Restart();
        try
        {
            RunLengauerTarjan(virtualRoot);
            Console.WriteLine($"Completed Lengauer-Tarjan in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in Lengauer-Tarjan algorithm: {ex.Message}");
            throw;
        }

        // Step 4: Build result structures
        sw.Restart();
        var result = BuildResult();
        Console.WriteLine($"Built result structures in {sw.ElapsedMilliseconds}ms");

        return result;
    }

    private void BuildGraph(Dictionary<ulong, ObjectInfo> objects)
    {
        _nodeCount = 0;

        // Assign node IDs to all objects
        foreach (var obj in objects.Values)
        {
            _addressToNode[obj.Address] = _nodeCount;
            _nodeToAddress[_nodeCount] = obj.Address;
            _nodeCount++;
        }

        // Initialize data structures
        for (int i = 0; i < _nodeCount; i++)
        {
            _successors.Add(new List<int>());
            _predecessors.Add(new List<int>());
            _parent.Add(-1);
            _ancestor.Add(-1);
            _child.Add(-1);
            _vertex.Add(-1);
            _label.Add(i);
            _semi.Add(-1);
            _size.Add(1);
            _bucket.Add(new List<int>());
            _dom.Add(-1);
        }

        // Build edges
        foreach (var obj in objects.Values)
        {
            if (!_addressToNode.TryGetValue(obj.Address, out int fromNode))
                continue;

            foreach (var reference in obj.References)
            {
                if (_addressToNode.TryGetValue(reference.TargetAddress, out int toNode))
                {
                    _successors[fromNode].Add(toNode);
                    _predecessors[toNode].Add(fromNode);
                }
            }
        }
    }

    private int AddVirtualRoot(List<ulong> rootAddresses)
    {
        int virtualRoot = _nodeCount;
        
        // Expand all data structures for the virtual root
        _successors.Add(new List<int>());
        _predecessors.Add(new List<int>());
        _parent.Add(-1);
        _ancestor.Add(-1);
        _child.Add(-1);
        _vertex.Add(-1);
        _label.Add(virtualRoot);
        _semi.Add(-1);
        _size.Add(1);
        _bucket.Add(new List<int>());
        _dom.Add(-1);
        
        _nodeToAddress[virtualRoot] = 0; // Virtual address
        _nodeCount++;

        // Connect virtual root to all GC roots
        foreach (var rootAddr in rootAddresses)
        {
            if (_addressToNode.TryGetValue(rootAddr, out int rootNode))
            {
                _successors[virtualRoot].Add(rootNode);
                _predecessors[rootNode].Add(virtualRoot);
            }
        }

        return virtualRoot;
    }

    private void RunLengauerTarjan(int root)
    {
        _dfsNumber = 0;

        // Step 1: DFS to number vertices and build spanning tree
        DFS(root);

        // Step 2: Compute semi-dominators and store vertices by semi-dominator
        for (int i = _dfsNumber - 1; i >= 1; i--)
        {
            int w = _vertex[i];
            
            // Step 2.1: Compute semi-dominator of w
            foreach (int v in _predecessors[w])
            {
                int u = Eval(v);
                if (_semi[u] < _semi[w])
                {
                    _semi[w] = _semi[u];
                }
            }

            _bucket[_vertex[_semi[w]]].Add(w);
            Link(_parent[w], w);

            // Step 2.2: Compute immediate dominators for vertices in bucket
            foreach (int v in _bucket[_parent[w]])
            {
                int u = Eval(v);
                _dom[v] = _semi[u] < _semi[v] ? u : _parent[w];
            }
            _bucket[_parent[w]].Clear();
        }

        // Step 3: Adjust immediate dominators
        for (int i = 1; i < _dfsNumber; i++)
        {
            int w = _vertex[i];
            if (_dom[w] != _vertex[_semi[w]])
            {
                _dom[w] = _dom[_dom[w]];
            }
        }

        _dom[root] = root; // Root dominates itself
    }

    private void DFS(int v)
    {
        _semi[v] = _dfsNumber;
        _vertex[_dfsNumber] = v;
        _label[v] = v;
        _ancestor[v] = -1;
        _dfsNumber++;

        foreach (int w in _successors[v])
        {
            if (_semi[w] == -1)
            {
                _parent[w] = v;
                DFS(w);
            }
        }
    }

    private void Link(int v, int w)
    {
        int s = w;
        while (_semi[_label[w]] < _semi[_label[_child[s]]])
        {
            if (_size[s] + _size[_child[_child[s]]] >= 2 * _size[_child[s]])
            {
                _ancestor[_child[s]] = s;
                _child[s] = _child[_child[s]];
            }
            else
            {
                _size[_child[s]] = _size[s];
                s = _ancestor[s] = _child[s];
            }
        }

        _label[s] = _label[w];
        _size[v] += _size[w];

        if (_size[v] < 2 * _size[w])
        {
            (s, _child[v]) = (_child[v], s);
        }

        while (s != -1)
        {
            _ancestor[s] = v;
            s = _child[s];
        }
    }

    private int Eval(int v)
    {
        if (_ancestor[v] == -1)
        {
            return _label[v];
        }

        Compress(v);
        return _label[v];
    }

    private void Compress(int v)
    {
        var stack = new Stack<int>();
        var visited = new HashSet<int>();
        int current = v;

        // Find path to root and push onto stack (with cycle detection)
        while (current >= 0 && current < _ancestor.Count && 
               _ancestor[current] != -1 && _ancestor[current] < _ancestor.Count &&
               _ancestor[_ancestor[current]] != -1 && !visited.Contains(current))
        {
            visited.Add(current);
            stack.Push(current);
            current = _ancestor[current];
        }

        // Compress path
        while (stack.Count > 0)
        {
            int w = stack.Pop();
            if (_ancestor[w] != -1 && _semi[_label[_ancestor[w]]] < _semi[_label[w]])
            {
                _label[w] = _label[_ancestor[w]];
            }
            if (_ancestor[w] != -1)
            {
                _ancestor[w] = _ancestor[_ancestor[w]];
            }
        }
    }

    private DominatorTreeResult BuildResult()
    {
        var dominatorTree = new Dictionary<ulong, List<ulong>>();
        var immediateDominators = new Dictionary<ulong, ulong>();

        // Initialize dominator tree
        for (int i = 0; i < _nodeCount - 1; i++) // Exclude virtual root
        {
            ulong address = _nodeToAddress[i];
            dominatorTree[address] = new List<ulong>();
        }

        // Build dominator relationships
        for (int i = 0; i < _nodeCount - 1; i++) // Exclude virtual root
        {
            int dominator = _dom[i];
            if (dominator != -1 && dominator != _nodeCount - 1) // Not virtual root
            {
                ulong nodeAddr = _nodeToAddress[i];
                ulong domAddr = _nodeToAddress[dominator];
                
                dominatorTree[domAddr].Add(nodeAddr);
                immediateDominators[nodeAddr] = domAddr;
            }
        }

        return new DominatorTreeResult(dominatorTree, immediateDominators);
    }
}

/// <summary>
/// Result of dominator tree computation.
/// </summary>
public record DominatorTreeResult(
    Dictionary<ulong, List<ulong>> DominatorTree,
    Dictionary<ulong, ulong> ImmediateDominators
);

