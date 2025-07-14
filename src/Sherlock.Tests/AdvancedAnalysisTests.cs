using NUnit.Framework;
using Sherlock.Testing;
using Sherlock.Core;

namespace Sherlock.Tests;

/// <summary>
/// Tests demonstrating advanced analysis features like dominator trees,
/// retained size calculations, and reference path finding.
/// These tests also serve as examples of the advanced heap analysis capabilities.
/// </summary>
[TestFixture]
public class AdvancedAnalysisTests
{
    [TearDown]
    public void TearDown()
    {
        ObjectGraph.Clear();
        TestUtilities.ForceGarbageCollection();
    }

    [Test]
    public async Task CanAccessOptimizedHeapStructures()
    {
        // Arrange: Create objects with known relationships
        var rootObjects = ObjectGraph.CreateConnectedObjects(20);

        // Act: Take snapshot and access optimized structures
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Should be able to access optimized structures
        Assert.DoesNotThrow(() =>
        {
            var optimized = snapshot.Optimized;
            Assert.That(optimized, Is.Not.Null);
        });

        // Cleanup
        rootObjects.Clear();
    }

    [Test]
    public async Task CanPerformSpatialQueries()
    {
        // Arrange: Create objects with varying sizes
        var objects = new List<SpatialTestObject>();
        for (int i = 0; i < 30; i++)
        {
            var size = (i % 3) switch
            {
                0 => 1024,   // Small objects
                1 => 4096,   // Medium objects  
                _ => 16384   // Large objects
            };
            objects.Add(new SpatialTestObject(size));
        }

        // Act: Take snapshot and perform spatial queries
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Should be able to query by size ranges
        Assert.DoesNotThrow(() =>
        {
            var smallObjects = snapshot.GetObjectsBySizeRange(512, 2048);
            Assert.That(smallObjects.Count(), Is.GreaterThan(5));

            var largeObjects = snapshot.GetObjectsBySizeRange(8192, 32768);
            Assert.That(largeObjects.Count(), Is.GreaterThan(5));
        });

        // Cleanup
        objects.Clear();
    }

    [Test]
    public async Task CanFindReferencePaths()
    {
        // Arrange: Create object graph with known references
        var objectGraph = ObjectGraph.CreateLinkedChain(15);

        // Act: Take snapshot and find reference paths
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Should be able to traverse object references
        var chainObjects = snapshot.GetObjectsByType(typeof(ChainedObject).FullName!).ToList();
        Assert.That(chainObjects.Count, Is.GreaterThanOrEqualTo(10));

        // Should be able to get references for objects
        if (chainObjects.Any())
        {
            var firstObject = chainObjects.First();
            var references = snapshot.GetIncomingReferences(firstObject.Address);
            // References might exist depending on object graph structure
            Assert.That(references, Is.Not.Null);
        }

        // Cleanup
        objectGraph.Clear();
    }

    [Test]
    public async Task CanAnalyzeTypeHierarchy()
    {
        // Arrange: Create objects with inheritance hierarchy
        var baseObjects = new List<BaseTestObject>();
        var derivedObjects = new List<DerivedTestObject>();

        for (int i = 0; i < 20; i++)
        {
            baseObjects.Add(new BaseTestObject($"Base-{i}"));
            derivedObjects.Add(new DerivedTestObject($"Derived-{i}", i));
        }

        // Act: Take snapshot and analyze type hierarchy
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Should find both base and derived types
        snapshot.Types().Where(typeof(BaseTestObject)).Instances.Should().BeGreaterThan(15);
        snapshot.Types().Where(typeof(DerivedTestObject)).Instances.Should().BeGreaterThan(15);

        // Should be able to get type hierarchy stats
        Assert.DoesNotThrow(() =>
        {
            var baseStats = snapshot.GetTypeHierarchyStats(typeof(BaseTestObject).FullName!);
            Assert.That(baseStats, Is.Not.Null);
        });

        // Cleanup
        baseObjects.Clear();
        derivedObjects.Clear();
    }

    [Test]
    public async Task CanGenerateComprehensiveReport()
    {
        // Arrange: Create diverse object types
        var testData = CreateDiverseObjectSet();

        // Act: Take snapshot and generate report
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();
        var report = snapshot.GenerateReport();

        // Assert: Report should contain meaningful data
        Assert.That(report, Is.Not.Null);
        Assert.That(report.TotalObjects, Is.GreaterThan(50));
        Assert.That(report.TotalMemory, Is.GreaterThan(1024));
        Assert.That(report.TypeStatistics, Is.Not.Empty);
        Assert.That(report.GenerationStatistics, Is.Not.Empty);

        // Should have statistics for our test types
        var reportedTypes = report.TypeStatistics.Select(ts => ts.TypeName).ToList();
        Assert.That(reportedTypes.Any(t => t.Contains("ReportTestObject")), Is.True);

        // Cleanup
        testData.Clear();
    }

    [Test]
    public async Task CanDetectLargestObjects()
    {
        // Arrange: Create objects of varying sizes including some very large ones
        var objects = new List<object>();
        
        // Create some large objects that should appear in "largest objects" list
        for (int i = 0; i < 5; i++)
        {
            objects.Add(new LargeTestObject(i, 64 * 1024)); // 64KB objects
        }
        
        // Create many smaller objects
        for (int i = 0; i < 100; i++)
        {
            objects.Add(new SmallTestObject(i));
        }

        // Act: Take snapshot and generate report
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();
        var report = snapshot.GenerateReport();

        // Assert: Large objects should appear in the largest objects list
        Assert.That(report.LargestObjects, Is.Not.Empty);
        
        var largestSizes = report.LargestObjects.Take(10).Select(o => o.Size).ToList();
        Assert.That(largestSizes.Any(size => size > 32 * 1024), Is.True, "Expected some large objects in the largest objects list");

        // Cleanup
        objects.Clear();
    }

    [Test]
    public async Task CanAnalyzeGenerationDistribution()
    {
        // Arrange: Create objects and force some GC to move objects between generations
        var gen0Objects = new List<object>();
        var gen1Objects = new List<object>();

        // Create initial objects (likely Gen 0)
        for (int i = 0; i < 50; i++)
        {
            gen0Objects.Add(new GenerationTestObject($"Gen0-{i}"));
        }

        // Force GC to promote some objects
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Create more objects (should be Gen 0)
        for (int i = 0; i < 30; i++)
        {
            gen1Objects.Add(new GenerationTestObject($"Gen1-{i}"));
        }

        // Act: Take snapshot and analyze generations
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();
        var report = snapshot.GenerateReport();

        // Assert: Should have generation statistics
        Assert.That(report.GenerationStatistics, Is.Not.Empty);
        Assert.That(report.GenerationStatistics.Any(gs => gs.Generation == 0), Is.True);
        
        var gen0Stats = report.GenerationStatistics.FirstOrDefault(gs => gs.Generation == 0);
        if (gen0Stats != null)
        {
            Assert.That(gen0Stats.ObjectCount, Is.GreaterThan(10));
            Assert.That(gen0Stats.TotalSize, Is.GreaterThan(1024));
        }

        // Cleanup
        gen0Objects.Clear();
        gen1Objects.Clear();
    }

    // Helper methods and test classes
    private static List<object> CreateDiverseObjectSet()
    {
        var objects = new List<object>();
        
        for (int i = 0; i < 25; i++)
        {
            objects.Add(new ReportTestObject($"Report-{i}", i * 100));
        }
        
        for (int i = 0; i < 15; i++)
        {
            objects.Add(new Dictionary<string, object> { ["key"] = $"value-{i}" });
        }
        
        for (int i = 0; i < 10; i++)
        {
            objects.Add(new List<int>(Enumerable.Range(0, i * 10)));
        }
        
        return objects;
    }
}

// Test infrastructure for object graphs
public static class ObjectGraph
{
    private static readonly List<object> _roots = new();

    public static List<ChainedObject> CreateLinkedChain(int length)
    {
        var chain = new List<ChainedObject>();
        ChainedObject? previous = null;

        for (int i = 0; i < length; i++)
        {
            var current = new ChainedObject($"Chain-{i}");
            if (previous != null)
            {
                previous.Next = current;
                current.Previous = previous;
            }
            chain.Add(current);
            previous = current;
        }

        _roots.AddRange(chain);
        return chain;
    }

    public static List<ConnectedObject> CreateConnectedObjects(int count)
    {
        var objects = new List<ConnectedObject>();
        
        for (int i = 0; i < count; i++)
        {
            objects.Add(new ConnectedObject($"Connected-{i}"));
        }

        // Create some connections
        for (int i = 0; i < objects.Count - 1; i++)
        {
            objects[i].AddConnection(objects[i + 1]);
            if (i > 0)
            {
                objects[i].AddConnection(objects[i - 1]);
            }
        }

        _roots.AddRange(objects);
        return objects;
    }

    public static void Clear()
    {
        _roots.Clear();
    }
}

// Test object classes
public class SpatialTestObject
{
    public byte[] Data { get; }

    public SpatialTestObject(int size)
    {
        Data = new byte[size];
    }
}

public class BaseTestObject
{
    public string Name { get; }
    public byte[] BaseData { get; }

    public BaseTestObject(string name)
    {
        Name = name;
        BaseData = new byte[256];
    }
}

public class DerivedTestObject : BaseTestObject
{
    public int Value { get; }
    public byte[] DerivedData { get; }

    public DerivedTestObject(string name, int value) : base(name)
    {
        Value = value;
        DerivedData = new byte[512];
    }
}

public class ChainedObject
{
    public string Name { get; }
    public ChainedObject? Next { get; set; }
    public ChainedObject? Previous { get; set; }
    public byte[] Data { get; }

    public ChainedObject(string name)
    {
        Name = name;
        Data = new byte[128];
    }
}

public class ConnectedObject
{
    public string Name { get; }
    public List<ConnectedObject> Connections { get; } = new();
    public byte[] Data { get; }

    public ConnectedObject(string name)
    {
        Name = name;
        Data = new byte[256];
    }

    public void AddConnection(ConnectedObject other)
    {
        Connections.Add(other);
    }
}

public class ReportTestObject
{
    public string Name { get; }
    public int Size { get; }
    public byte[] Data { get; }

    public ReportTestObject(string name, int size)
    {
        Name = name;
        Size = size;
        Data = new byte[Math.Max(size, 64)];
    }
}

public class LargeTestObject
{
    public int Id { get; }
    public byte[] LargeData { get; }

    public LargeTestObject(int id, int size)
    {
        Id = id;
        LargeData = new byte[size];
    }
}

public class SmallTestObject
{
    public int Id { get; }
    public byte[] SmallData { get; }

    public SmallTestObject(int id)
    {
        Id = id;
        SmallData = new byte[64];
    }
}

public class GenerationTestObject
{
    public string Name { get; }
    public byte[] Data { get; }

    public GenerationTestObject(string name)
    {
        Name = name;
        Data = new byte[128];
    }
}