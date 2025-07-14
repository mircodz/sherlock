using NUnit.Framework;
using Sherlock.Testing;

namespace Sherlock.Tests;

/// <summary>
/// Basic memory leak detection tests demonstrating core Sherlock functionality.
/// These tests serve as both validation and examples of how to use the library.
/// </summary>
[TestFixture]
public class BasicLeakTests
{
    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        // Cleanup any dump files created during tests
        TestUtilities.CleanupDumpFiles();
    }
    
    [Test]
    public async Task CanDetectByteArrayInstances()
    {
        // Arrange: Create some byte arrays
        var arrays = new List<byte[]>();
        for (int i = 0; i < 50; i++)
        {
            arrays.Add(new byte[1024]);
        }

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Should find our byte arrays
        snapshot.Types().Where(typeof(byte[])).Instances.Should().BeGreaterThan(40);
        
        // Cleanup
        arrays.Clear();
    }

    [Test]
    public async Task CanDetectCustomObjectInstances()
    {
        // Arrange: Create custom objects
        var objects = new List<TestObject>();
        for (int i = 0; i < 25; i++)
        {
            objects.Add(new TestObject($"Test-{i}"));
        }

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Should find our test objects
        snapshot.Types().Where(typeof(TestObject)).Instances.Should().BeGreaterThan(20);
        
        // Cleanup
        objects.Clear();
    }

    [Test]
    public async Task ObjectsAreCollectedAfterGC()
    {
        // Arrange: Create objects in a scope that will release them
        CreateTemporaryObjects();

        // Force garbage collection
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot after GC
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Temporary objects should be collected
        // Note: We can't guarantee zero instances due to other code, but should be minimal
        var count = snapshot.Types().Where(typeof(TemporaryTestObject)).Instances;
        Assert.That(count, Is.LessThan(5), $"Expected few TemporaryTestObjects after GC, but found {count}");
    }

    [Test]
    public async Task FluentAPIBasicUsage()
    {
        // Arrange: Create known objects
        var strings = new List<string>();
        for (int i = 0; i < 30; i++)
        {
            strings.Add($"Test string {i} with some content to make it unique");
        }

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Demonstrate fluent API
        snapshot.Types().Where(typeof(string)).Instances.Should().BeGreaterThan(25);
        
        // Alternative syntax
        snapshot.Types().Where(t => t.Contains("String")).Instances.Should().BeGreaterThan(0);
        
        // Cleanup
        strings.Clear();
    }

    [Test]
    public async Task EmptyCollectionTest()
    {
        // Arrange: Start with clean state
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Should have minimal instances of our test types
        snapshot.Types().Where(typeof(NonExistentTestType)).Should().BeEmpty();
    }

    // Helper method to create temporary objects
    private static void CreateTemporaryObjects()
    {
        var temp = new List<TemporaryTestObject>();
        for (int i = 0; i < 20; i++)
        {
            temp.Add(new TemporaryTestObject());
        }
        // Objects go out of scope here
    }

    // Test classes
    private class TestObject
    {
        public string Name { get; }
        public byte[] Data { get; }

        public TestObject(string name)
        {
            Name = name;
            Data = new byte[512];
        }
    }

    private class TemporaryTestObject
    {
        public byte[] Data { get; } = new byte[256];
    }

    private class NonExistentTestType
    {
        // This type should never be instantiated in these tests
    }
}