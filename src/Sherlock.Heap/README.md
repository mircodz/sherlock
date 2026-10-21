# Sherlock.Heap

Fast analysis of the managed heap in .NET memory dumps: the engine behind `sl`'s heap commands, usable on its own.
It reads the runtime's metadata through [ClrMD](https://github.com/microsoft/clrmd) and walks the heap on all cores,
straight from the memory-mapped dump. On a 14-core laptop, a histogram of a 4 GB heap takes 0.5 s, against 7 s with
ClrMD.

> **Preview.** It isn't on NuGet yet, and its API will change before it is.

## Getting started

Reference the project from a .NET 10 app:

```xml
<ProjectReference Include="path/to/sherlock/src/Sherlock.Heap/Sherlock.Heap.csproj" />
```

Open a dump with heap memory (what `dotnet-dump collect` writes by default) and query it:

```csharp
using Sherlock.Heap;

using HeapDump dump = HeapDump.Open("app.dmp", cacheDirectory: "app.dmp.cache");

foreach (HeapTypeStat type in dump.Histogram.Take(10))
{
    Console.WriteLine($"{type.TotalSize,12:N0} B  {type.Count,9:N0}  {type.TypeName}");
}

foreach (ObjectInstance order in dump.Instances("MyApp.Order", limit: 3, exact: true).Instances)
{
    foreach (GcRootPath path in dump.Roots(order.Address))
    {
        Console.WriteLine($"{path.Root.Kind} → {string.Join(" → ", path.Path.Select(node => node.TypeName))}");
    }
}
```

`Open` throws `FileNotFoundException` for a missing file and `DumpAnalysisException` for a dump without a .NET
runtime. `Inspect` throws `DumpAnalysisException` for an address that isn't an object.

## Queries

| Member | Answers |
|---|---|
| `Info`, `Modules`, `Threads`, `Segments`, `Generations` | the runtime, its modules and threads, and the GC heap's layout |
| `Histogram` | objects per type, largest total size first |
| `Instances(filter, limit)` | the largest objects whose type name contains `filter` (or equals it, with `exact: true`), and totals over every match |
| `Inspect(address)`, `InspectValue`, `InspectChildren` | an object's fields, string contents, or elements |
| `Roots(address)` | every GC root keeping an object alive, each with a shortest path to it |
| `Dominators` | what each object retains, and the objects retaining the most |
| `DuplicateStrings()` | string values stored more than once, by wasted bytes |
| `Exceptions`, `Finalizers()`, `EventHandlerLeaks()` | exception objects, finalizable objects by type, events with many subscribers |

For anything else, `dump.Runtime` is the dump's ClrMD `ClrRuntime`. The dump owns it: don't dispose it.

## Caches

`Roots` and `Dominators` first build a reference graph and its indexes in the cache directory. That takes a few
seconds per GB of heap and about as much disk as the heap; later opens of the same dump reuse them, and `Histogram`
and `Instances` read the graph when there is one. Give each dump its own cache directory: a different or modified dump
rebuilds the indexes. Scratch files go there too.

## Threads and lifetime

- Queries can run on several threads at once. Each cached result is computed once, and concurrent callers wait for it.
- Long queries accept a `CancellationToken`; for `Histogram`, `Exceptions`, and `Dominators`, call `GetHistogram`,
  `GetExceptions`, and `GetDominatorTree`.
- Results backed by index files, like the dominator tree, are valid until the dump is disposed.
