using System.Runtime.InteropServices;

// A small heap of known shapes for the heap walk and graph tests: a long chain of objects, strings, dictionaries,
// arrays of structs holding references, a pinned buffer, a large array whose untouched tail a dump may leave out,
// events with many subscribers, caught exceptions kept alive, and repeated strings. Prints "ready" once built, then
// waits for stdin to close.

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
var publishers = new Publisher[3];
for (int p = 0; p < publishers.Length; p++)
{
    publishers[p] = new Publisher();
    for (int s = 0; s < 20 * (p + 1); s++)
    {
        publishers[p].Changed += s % 2 == 0 ? nodes[s].OnChanged : Publisher.OnStatic;
    }
}
var errors = new List<Exception>();
foreach (string message in new[] { "first", "second" })
{
    try
    {
        throw new InvalidOperationException(message);
    }
    catch (Exception ex)
    {
        errors.Add(ex);
    }
}
var copies = new List<string>();
for (int i = 0; i < 3_000; i++)
{
    copies.Add(new string('x', 40));
}
for (int i = 0; i < 500; i++)
{
    copies.Add(string.Concat("city-", (i % 5).ToString()));
}
copies.Add(new string('y', 100_000));
copies.Add(new string('y', 100_000));
GC.Collect();

Console.WriteLine("ready");
Console.In.ReadToEnd();
GC.KeepAlive(nodes);
GC.KeepAlive(pairs);
GC.KeepAlive(sparse);
GC.KeepAlive(publishers);
GC.KeepAlive(errors);
GC.KeepAlive(copies);
pinned.Free();

sealed record Node(string Name, Node? Next, Dictionary<string, object>? Properties)
{
    public void OnChanged(object? sender, EventArgs e)
    {
    }
}

sealed class Publisher
{
    public event EventHandler? Changed;

    public static void OnStatic(object? sender, EventArgs e)
    {
    }

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
