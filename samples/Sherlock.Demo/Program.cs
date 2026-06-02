using System.Diagnostics;

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
            Thread.Sleep(Timeout.Infinite);
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
            File.Delete(dumpPath);

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
    public List<Customer> Customers { get; } = new();

    public void Add(Customer customer) => Customers.Add(customer);

    public int TotalOrders()
    {
        int total = 0;
        foreach (Customer customer in Customers)
            total += customer.Orders.Count;
        return total;
    }
}

internal sealed class Customer(int id, string name, string email)
{
    public int Id { get; } = id;
    public string Name { get; } = name;
    public string Email { get; } = email;
    public List<Order> Orders { get; } = new();
}

internal sealed record Order(int Id, string Description, decimal Amount);

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
            return 1;

        process.WaitForExit();
        return process.ExitCode;
    }
}
