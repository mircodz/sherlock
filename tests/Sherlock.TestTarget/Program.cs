using System.Runtime.InteropServices;

// A small heap of known shapes for the heap walk and graph tests: a long chain of objects, strings, dictionaries,
// arrays of structs holding references, a pinned buffer, and a large array whose untouched tail a dump may leave
// out. Prints "ready" once built, then waits for stdin to close.

var nodes = new Node[50_000];
for (int i = 0; i < nodes.Length; i++)
{
    nodes[i] = new Node($"node-{i}", i > 0 ? nodes[i - 1] : null, i % 10 == 0 ? new Dictionary<string, object> { ["index"] = i } : null);
}
var pairs = new KeyValuePair<string, object?>[20_000];
for (int i = 0; i < pairs.Length; i++)
{
    pairs[i] = new($"key-{i}", nodes[i * 2]);
}
var sparse = new object?[4_000_000];
for (int i = 0; i < 1_000; i++)
{
    sparse[i] = nodes[i];
}
GCHandle pinned = GCHandle.Alloc(new byte[1024], GCHandleType.Pinned);
GC.Collect();

Console.WriteLine("ready");
Console.In.ReadToEnd();
GC.KeepAlive(nodes);
GC.KeepAlive(pairs);
GC.KeepAlive(sparse);
pinned.Free();

sealed record Node(string Name, Node? Next, Dictionary<string, object>? Properties);
