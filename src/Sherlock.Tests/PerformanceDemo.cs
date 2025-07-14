using NUnit.Framework;
using Sherlock.Testing;
using System.Diagnostics;

namespace Sherlock.Tests;

/// <summary>
/// Demonstrates the performance improvements with lazy evaluation.
/// </summary>
[TestFixture]
public class PerformanceDemo
{
    [Test]
    public async Task LazyEvaluationDemo()
    {
        // Arrange: Create test objects
        var testObjects = new List<TestObject>();
        for (int i = 0; i < 100; i++)
        {
            testObjects.Add(new TestObject($"Test-{i}"));
        }

        // Act: Take snapshot with lazy evaluation
        var sw = Stopwatch.StartNew();
        var snapshot = await TestUtilities.TakeSnapshotAsync();
        sw.Stop();
        
        Console.WriteLine($"Snapshot creation time: {sw.ElapsedMilliseconds}ms");

        // Assert: Exact type query - scans only for TestObject
        sw.Restart();
        var count = snapshot.Types().Where(typeof(TestObject)).Instances;
        sw.Stop();
        
        Console.WriteLine($"Exact type count time: {sw.ElapsedMilliseconds}ms (scans only for TestObject)");
        Console.WriteLine($"Found {count} TestObject instances");

        // Demonstrate predicate filtering - builds type names index then filters
        sw.Restart();
        var stringCount = snapshot.Types().Where(t => t.Contains("String")).Instances;
        sw.Stop();
        
        Console.WriteLine($"Predicate filtering time: {sw.ElapsedMilliseconds}ms (builds type names index + scans for matches)");
        Console.WriteLine($"Found {stringCount} String-related instances");

        // Subsequent exact queries should be instant (already scanned)
        sw.Restart();
        var count2 = snapshot.Types().Where(typeof(TestObject)).Instances;
        sw.Stop();
        
        Console.WriteLine($"Second exact type query: {sw.ElapsedMilliseconds}ms (already scanned, instant lookup)");

        // Demonstrate inspection
        sw.Restart();
        var inspection = snapshot.Types()
            .Where(typeof(TestObject))
            .Inspect(0, new InspectOptions { Format = InspectFormat.Compact });
        sw.Stop();
        
        Console.WriteLine($"Inspection time: {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"Inspection result: {inspection}");

        // Assertions
        Assert.That(count, Is.GreaterThan(90));
        Assert.That(count, Is.EqualTo(count2)); // Should be same
        Assert.That(inspection, Contains.Substring("TestObject"));
        
        // Cleanup
        testObjects.Clear();
    }
    
    [Test]
    public async Task IntelligentScanningDemo()
    {
        // Arrange: Create objects of different types
        var objects = new List<object>
        {
            new TestObject("test1"),
            new TempObject(),
            new TestObject("test2"),
            "some string",
            new byte[1024]
        };

        var snapshot = await TestUtilities.TakeSnapshotAsync();
        
        Console.WriteLine("\n=== Intelligent Scanning Demo ===");
        
        // Demo 1: Exact type query - only scans for that specific type
        var sw = Stopwatch.StartNew();
        var testObjectCount = snapshot.Types().Where(typeof(TestObject)).Instances;
        sw.Stop();
        Console.WriteLine($"TestObject scan: {sw.ElapsedMilliseconds}ms (found {testObjectCount})");
        
        // Demo 2: Different exact type - scans only for this new type
        sw.Restart();
        var byteArrayCount = snapshot.Types().Where(typeof(byte[])).Instances;
        sw.Stop();
        Console.WriteLine($"byte[] scan: {sw.ElapsedMilliseconds}ms (found {byteArrayCount})");
        
        // Demo 3: Predicate query - builds type names index, then scans for matches
        sw.Restart();
        var testRelatedCount = snapshot.Types().Where(t => t.Contains("Test")).Instances;
        sw.Stop();
        Console.WriteLine($"Predicate scan (types containing 'Test'): {sw.ElapsedMilliseconds}ms (found {testRelatedCount})");
        
        // Demo 4: Repeated query - should be instant (already scanned)
        sw.Restart();
        var testObjectCount2 = snapshot.Types().Where(typeof(TestObject)).Instances;
        sw.Stop();
        Console.WriteLine($"Repeated TestObject query: {sw.ElapsedMilliseconds}ms (cached, found {testObjectCount2})");
        
        Assert.That(testObjectCount, Is.EqualTo(testObjectCount2));
        
        // Cleanup
        objects.Clear();
    }

    [Test]
    public async Task GCForcingDemo()
    {
        // Arrange: Create objects that should be collected
        CreateTemporaryObjects();

        // Act: Take snapshot with GC forcing enabled (default)
        var snapshot = await TestUtilities.TakeSnapshotAsync();
        
        // Assert: Objects should be collected due to GC forcing
        var count = snapshot.Types().Where(typeof(TempObject)).Instances;
        Console.WriteLine($"Found {count} TempObject instances after GC");
        
        // Should have minimal instances due to GC
        Assert.That(count, Is.LessThan(5));
        
        // Demonstrate: Take snapshot without GC forcing
        CreateTemporaryObjects();
        var snapshotNoGC = await Testing.Sherlock.SnapshotAsync(forceGC: false);
        var countNoGC = snapshotNoGC.GetTypeCount(typeof(TempObject).FullName ?? typeof(TempObject).Name);
        Console.WriteLine($"Found {countNoGC} TempObject instances without GC");
        
        // Should have more instances without GC
        Assert.That(countNoGC, Is.GreaterThan(count));
    }

    private static void CreateTemporaryObjects()
    {
        var temp = new List<TempObject>();
        for (int i = 0; i < 50; i++)
        {
            temp.Add(new TempObject());
        }
        // Objects go out of scope here
    }

    private class TestObject
    {
        public string Name { get; }
        public byte[] Data { get; }

        public TestObject(string name)
        {
            Name = name;
            Data = new byte[1024];
        }
    }

    private class TempObject
    {
        public byte[] Data { get; } = new byte[512];
    }
}