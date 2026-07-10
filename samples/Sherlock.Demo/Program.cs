using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Sherlock.Demo;

/// <summary>
/// Allocates a recognizable, rooted object graph and then writes a memory dump
/// of itself so the Sherlock analyzer has something interesting to inspect.
///
/// Usage:
///   dotnet run --project samples/Sherlock.Demo -- [output-dump-path]
///
/// The graph is anchored by <see cref="Registry"/>, a static field — i.e. a
/// strong GC root — so `gcroot` against any contained object resolves to it.
/// </summary>
internal static class Program
{
    // A static field is a GC root; everything reachable from here stays alive.
    private static readonly CustomerRegistry Registry = new();

    private static int Main(string[] args)
    {
        // `--serve`: build the graph and stay alive so `sherlock collect` can dump us.
        if (args.Length > 0 && args[0] == "--serve")
        {
            BuildGraph();
            GC.Collect();
            Console.WriteLine($"Demo serving. PID {Environment.ProcessId}. " +
                              $"Holding {Registry.Customers.Count} customers; press Ctrl-C to stop.");
            Console.WriteLine($"  sherlock collect --pid {Environment.ProcessId} --analyze");
            Thread.Sleep(Timeout.Infinite);
            GC.KeepAlive(Registry);
            return 0;
        }

        // `--grow`: allocate steadily for ~10s, then hold — for watching live allocations.
        if (args.Length > 0 && args[0] == "--grow")
        {
            Console.WriteLine($"Demo growing. PID {Environment.ProcessId}. Snapshot me over time.");
            for (int round = 1; round <= 10; round++)
            {
                AddCustomers(200);
                Console.WriteLine($"round {round}: {Registry.Customers.Count} customers, " +
                                  $"{Registry.TotalOrders()} orders");
                Thread.Sleep(1000);
            }
            Console.WriteLine("Done growing; holding. Ctrl-C to stop.");
            Thread.Sleep(3000);
            GC.KeepAlive(Registry);
            return 0;
        }

        // `--crash`: build the graph and throw, to exercise crash-dump capture.
        if (args.Length > 0 && args[0] == "--crash")
        {
            BuildGraph();
            Console.WriteLine($"Demo crashing. PID {Environment.ProcessId}, " +
                              $"holding {Registry.Customers.Count} customers.");
            throw new InvalidOperationException("Demo intentional crash with a live object graph.");
        }

        // `--throw`: build the graph, throw+catch a DemoException (so it fires an exception
        // trigger but stays alive), then hold — for testing `snapshot-on throw:...`.
        if (args.Length > 0 && args[0] == "--throw")
        {
            BuildGraph();
            Console.WriteLine($"Demo throwing. PID {Environment.ProcessId}, holding {Registry.Customers.Count} customers.");
            try
            {
                throw new DemoException("Demo trigger exception.");
            }
            catch (DemoException)
            {
                Console.WriteLine("Caught DemoException; holding. Ctrl-C to stop.");
            }
            Thread.Sleep(Timeout.Infinite);
            GC.KeepAlive(Registry);
            return 0;
        }

        // `--work [seconds]`: a varied, self-driving workload — steadily leaks into the static
        // registry, churns transient garbage, and periodically GCs. Observe it live from the
        // Sherlock REPL (`allocations`, `snapshot`, `whoalloc`), then it exits cleanly.
        if (args.Length > 0 && args[0] == "--work")
        {
            int seconds = args.Length > 1 && int.TryParse(args[1], out int s) ? s : 15;
            Console.WriteLine($"Demo working for {seconds}s. PID {Environment.ProcessId}. " +
                              "Observe with sl: allocations, snapshot, whoalloc.");
            var stopwatch = Stopwatch.StartNew();
            var transient = new List<byte[]>();
            for (int round = 1; stopwatch.Elapsed.TotalSeconds < seconds; round++)
            {
                AddCustomers(100);                     // permanent: leaks into the static graph
                transient.Add(new byte[64 * 1024]);    // transient: collectable garbage

                // Rotate through distinct allocation paths so the call tree / hot methods /
                // back-traces have variety (strings, LINQ, dictionaries, boxing).
                switch (round % 3)
                {
                    case 0: BuildReport(); break;
                    case 1: ComputeOrderStats(); break;
                    default: IndexByEmail(); break;
                }

                if (round % 5 == 0)
                {
                    transient.Clear();
                    GC.Collect();
                    Console.WriteLine($"round {round}: GC'd; holding {Registry.Customers.Count} customers, " +
                                      $"{Registry.TotalOrders()} orders");
                }
                Thread.Sleep(300);
            }
            // End-of-work sentinel: throw a (caught) DemoException to mark a capture point, then
            // hold briefly so `snapshot-on throw:Sherlock.Demo.DemoException` can dump the final
            // heap — a stand-in for "snapshot on exit" while the process is still fully alive.
            Console.WriteLine($"Done ({Registry.Customers.Count} customers). Firing end-of-work marker; holding 5s.");
            try { throw new DemoException("workload complete"); }
            catch (DemoException) { /* marker only */ }
            Thread.Sleep(5000);
            GC.KeepAlive(Registry);
            return 0;
        }

        // `--spawn [n] [child-mode]`: launch n child copies of the demo (default `--work`). They
        // inherit the profiler env, so `run --profile --children -- <demo> --spawn 3` profiles the
        // parent and every child, each into its own per-pid file.
        if (args.Length > 0 && args[0] == "--spawn")
        {
            int n = args.Length > 1 && int.TryParse(args[1], out int c) ? c : 2;
            string childMode = args.Length > 2 ? args[2] : "--work";
            Console.WriteLine($"Spawning {n} child workers ({childMode}). Parent PID {Environment.ProcessId}.");
            var children = new List<Process>();
            for (int i = 0; i < n; i++)
            {
                var childPsi = new ProcessStartInfo(Environment.ProcessPath!);
                childPsi.ArgumentList.Add(childMode);
                childPsi.ArgumentList.Add("8"); // seconds
                if (Process.Start(childPsi) is { } child)
                {
                    children.Add(child);
                    Console.WriteLine($"  child pid {child.Id}");
                }
            }
            BuildGraph(); // the parent holds a graph of its own too
            foreach (Process child in children)
            {
                child.WaitForExit();
            }
            Console.WriteLine("All children exited.");
            GC.KeepAlive(Registry);
            return 0;
        }

        string dumpPath = args.Length > 0
            ? args[0]
            : Path.Combine(Path.GetTempPath(), "sherlock-demo.dmp");

        BuildGraph();

        // Keep the graph reachable across the dump call.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Console.WriteLine($"Allocated {Registry.Customers.Count} customers " +
                          $"holding {Registry.TotalOrders()} orders.");
        Console.WriteLine($"PID {Environment.ProcessId}. Writing dump to {dumpPath} …");

        if (File.Exists(dumpPath))
        {
            File.Delete(dumpPath);
        }

        int code = CreateDump.OfSelf(dumpPath);
        if (code != 0)
        {
            Console.Error.WriteLine($"createdump failed with exit code {code}.");
            return code;
        }

        Console.WriteLine($"Dump written. Analyze it with:");
        Console.WriteLine($"  dotnet run --project src/Sherlock.CLI -- \"{dumpPath}\"");

        // Reference the graph after the dump so the JIT can't elide it early.
        GC.KeepAlive(Registry);
        return 0;
    }

    private static void BuildGraph() => AddCustomers(500);

    /// <summary>String-heavy work: interpolation, StringBuilder, Split/Join — transient strings.</summary>
    private static string BuildReport()
    {
        var sb = new StringBuilder();
        foreach (Customer customer in Registry.Customers)
        {
            sb.AppendLine($"{customer.Id},{customer.Name},{customer.Email},{customer.Orders.Count}");
        }
        string report = sb.ToString();
        return string.Join("|", report.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>LINQ-heavy work: Select/Where/OrderBy/ToList over the order graph.</summary>
    private static void ComputeOrderStats()
    {
        var stats = Registry.Customers
            .Select(c => new { c.Name, Total = c.Orders.Sum(o => o.Amount), Count = c.Orders.Count })
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Total)
            .ToList();
        _ = stats.Take(10).ToArray();
    }

    /// <summary>Collection-heavy work: a dictionary index plus int→object boxing.</summary>
    private static void IndexByEmail()
    {
        var index = new Dictionary<string, Customer>();
        var ids = new List<object>();
        foreach (Customer customer in Registry.Customers)
        {
            index[customer.Email] = customer;
            ids.Add(customer.Id); // boxes the int
        }
        _ = index.Count + ids.Count;
    }

    private static void AddCustomers(int count)
    {
        int start = Registry.Customers.Count;
        for (int c = start; c < start + count; c++)
        {
            var customer = new Customer(c, $"Customer #{c}", $"customer{c}@example.com");
            for (int o = 0; o < 20; o++)
            {
                customer.Orders.Add(new Order(
                    Id: c * 1000 + o,
                    Description: $"Order {o} for customer {c}",
                    Amount: (decimal)((o + 1) * 9.99)));
            }
            Registry.Add(customer);
        }
    }
}

internal sealed class CustomerRegistry
{
    public List<Customer> Customers { get; } = [];

    public void Add(Customer customer) => Customers.Add(customer);

    public int TotalOrders()
    {
        int total = 0;
        foreach (Customer customer in Customers)
        {
            total += customer.Orders.Count;
        }

        return total;
    }
}

internal sealed class Customer(int id, string name, string email)
{
    public int Id { get; } = id;
    public string Name { get; } = name;
    public string Email { get; } = email;
    public List<Order> Orders { get; } = [];
}

internal sealed record Order(int Id, string Description, decimal Amount);

/// <summary>A distinct exception type so <c>snapshot-on throw:Sherlock.Demo.DemoException</c> can target it.</summary>
internal sealed class DemoException(string message) : Exception(message);

/// <summary>Thin wrapper that invokes the runtime's <c>createdump</c> on the current process.</summary>
internal static class CreateDump
{
    public static int OfSelf(string outputPath)
    {
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string createDump = Path.Combine(runtimeDir,
            OperatingSystem.IsWindows() ? "createdump.exe" : "createdump");

        if (!File.Exists(createDump))
        {
            Console.Error.WriteLine($"createdump not found at {createDump}.");
            return 1;
        }

        // -f <path>: output file, --full: include the full managed heap.
        var psi = new ProcessStartInfo(createDump)
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(outputPath);
        psi.ArgumentList.Add("--full");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());

        using Process? process = Process.Start(psi);
        if (process is null)
        {
            return 1;
        }

        process.WaitForExit();
        return process.ExitCode;
    }
}
