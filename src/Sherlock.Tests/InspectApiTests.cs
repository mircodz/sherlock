using NUnit.Framework;
using Sherlock.Testing;
using System.Collections.Generic;

namespace Sherlock.Tests;

/// <summary>
/// Tests for the object inspection API with complex object hierarchies.
/// </summary>
[TestFixture]
public class InspectApiTests
{
    [Test]
    public async Task ComplexObjectInspectionDemo()
    {
        // Arrange: Create a complex object hierarchy
        var complexObjects = CreateComplexObjectHierarchy();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeSnapshotAsync();

        // Assert: Demonstrate different inspection formats
        Console.WriteLine("=== COMPACT FORMAT ===");
        var compactInspection = snapshot.Types()
            .Where(typeof(ComplexObject))
            .Inspect(0, new InspectOptions { Format = InspectFormat.Compact });
        
        Console.WriteLine(compactInspection);

        Console.WriteLine("\n=== DETAILED FORMAT ===");
        var detailedInspection = snapshot.Types()
            .Where(typeof(ComplexObject))
            .Inspect(0, new InspectOptions 
            { 
                Format = InspectFormat.Detailed,
                ShowFields = true,
                ShowReferences = true,
                MaxDepth = 2,
                MaxFields = 10,
                MaxReferences = 5
            });
        
        Console.WriteLine(detailedInspection);

        Console.WriteLine("\n=== JSON FORMAT ===");
        var jsonInspection = snapshot.Types()
            .Where(typeof(ComplexObject))
            .Inspect(0, new InspectOptions { Format = InspectFormat.Json });
        
        Console.WriteLine(jsonInspection);

        Console.WriteLine("\n=== DEBUG FORMAT ===");
        var debugInspection = snapshot.Types()
            .Where(typeof(ComplexObject))
            .Inspect(0, new InspectOptions { Format = InspectFormat.Debug });
        
        Console.WriteLine(debugInspection);

        // Assertions
        Assert.That(compactInspection, Contains.Substring("ComplexObject"));
        Assert.That(detailedInspection, Contains.Substring("<Name>k__BackingField:"));
        Assert.That(detailedInspection, Contains.Substring("<Items>k__BackingField:"));
        Assert.That(detailedInspection, Contains.Substring("<Owner>k__BackingField:"));
        Assert.That(detailedInspection, Contains.Substring("Complex_0"));
        Assert.That(jsonInspection, Contains.Substring("ComplexObject"));
        Assert.That(debugInspection, Contains.Substring("Fields"));
        
        // Cleanup
        complexObjects.Clear();
    }

    [Test]
    public async Task NestedObjectInspectionTest()
    {
        // Arrange: Create nested object structure
        var root = new RootObject
        {
            Name = "Root",
            Child = new ChildObject
            {
                Value = 42,
                Data = new byte[] { 1, 2, 3, 4, 5 },
                Tags = new[] { "tag1", "tag2", "tag3" },
                Nested = new NestedObject
                {
                    Secret = "hidden_value",
                    Timestamp = DateTime.Now
                }
            }
        };

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeSnapshotAsync();

        // Assert: Inspect with different depth levels
        Console.WriteLine("=== DEPTH 1 ===");
        var depth1 = snapshot.Types()
            .Where(typeof(RootObject))
            .Inspect(0, new InspectOptions 
            { 
                MaxDepth = 1,
                ShowFields = true,
                ShowReferences = true
            });
        Console.WriteLine(depth1);

        Console.WriteLine("\n=== DEPTH 3 ===");
        var depth3 = snapshot.Types()
            .Where(typeof(RootObject))
            .Inspect(0, new InspectOptions 
            { 
                MaxDepth = 3,
                ShowFields = true,
                ShowReferences = true
            });
        Console.WriteLine(depth3);

        // Assertions
        Assert.That(depth1, Contains.Substring("RootObject"));
        Assert.That(depth1, Contains.Substring("<Name>k__BackingField:"));
        Assert.That(depth3, Contains.Substring("<Secret>k__BackingField:")); // Should be visible at depth 3
        Assert.That(depth3, Contains.Substring("hidden_value"));
        
        // Keep reference to prevent GC
        GC.KeepAlive(root);
    }

    [Test]
    public async Task CollectionInspectionTest()
    {
        // Arrange: Create objects with collections
        var listContainer = new ListContainer
        {
            Numbers = new List<int> { 1, 2, 3, 4, 5 },
            Names = new List<string> { "Alice", "Bob", "Charlie" },
            Dictionary = new Dictionary<string, int>
            {
                { "one", 1 },
                { "two", 2 },
                { "three", 3 }
            }
        };

        var arrayContainer = new ArrayContainer
        {
            Bytes = new byte[] { 0x01, 0x02, 0x03, 0xFF },
            Objects = new object[] { "string", 42, new DateTime(2023, 1, 1) }
        };

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeSnapshotAsync();

        // Assert: Inspect collections
        Console.WriteLine("=== LIST CONTAINER ===");
        var listInspection = snapshot.Types()
            .Where(typeof(ListContainer))
            .Inspect(0, new InspectOptions 
            { 
                Format = InspectFormat.Detailed,
                ShowFields = true,
                MaxFields = 20
            });
        Console.WriteLine(listInspection);

        Console.WriteLine("\n=== ARRAY CONTAINER ===");
        var arrayInspection = snapshot.Types()
            .Where(typeof(ArrayContainer))
            .Inspect(0, new InspectOptions 
            { 
                Format = InspectFormat.Detailed,
                ShowFields = true
            });
        Console.WriteLine(arrayInspection);

        // Assertions
        Assert.That(listInspection, Contains.Substring("<Numbers>k__BackingField:"));
        Assert.That(listInspection, Contains.Substring("<Names>k__BackingField:"));
        Assert.That(arrayInspection, Contains.Substring("<Bytes>k__BackingField:"));
        Assert.That(arrayInspection, Contains.Substring("<Objects>k__BackingField:"));
        
        // Keep references to prevent GC
        GC.KeepAlive(listContainer);
        GC.KeepAlive(arrayContainer);
    }

    [Test]
    public async Task InspectByAddressTest()
    {
        // Arrange: Create a simple object
        var testObj = new SimpleObject { Name = "AddressTest", Value = 123 };

        // Act: Take snapshot and get object addresses
        var snapshot = await TestUtilities.TakeSnapshotAsync();
        var obj = snapshot.GetObjectsByType(typeof(SimpleObject).FullName!).FirstOrDefault();
        
        Assert.That(obj, Is.Not.Null, "Should find the test object");

        // Assert: Inspect by address
        var addressInspection = snapshot.Inspect(obj.Address, new InspectOptions
        {
            Format = InspectFormat.Detailed,
            ShowFields = true
        });

        Console.WriteLine("=== INSPECT BY ADDRESS ===");
        Console.WriteLine(addressInspection);

        Assert.That(addressInspection, Contains.Substring("SimpleObject"));
        Assert.That(addressInspection, Contains.Substring("<Name>k__BackingField:"));
        Assert.That(addressInspection, Contains.Substring("AddressTest"));
        
        // Keep reference to prevent GC
        GC.KeepAlive(testObj);
    }

    private static List<ComplexObject> CreateComplexObjectHierarchy()
    {
        var owner = new Person { Name = "John Doe", Age = 30 };
        var items = new List<string> { "item1", "item2", "item3" };
        var metadata = new Dictionary<string, object>
        {
            { "created", DateTime.Now },
            { "version", 1.0 },
            { "enabled", true }
        };

        var complexObjects = new List<ComplexObject>();
        
        for (int i = 0; i < 3; i++)
        {
            var complex = new ComplexObject
            {
                Id = i,
                Name = $"Complex_{i}",
                Owner = owner,
                Items = new List<string>(items),
                Metadata = new Dictionary<string, object>(metadata),
                Data = new byte[100 + i * 50],
                IsActive = i % 2 == 0
            };
            
            complexObjects.Add(complex);
        }

        return complexObjects;
    }

    // Test classes with various field types
    private class ComplexObject
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public Person? Owner { get; set; }
        public List<string> Items { get; set; } = new();
        public Dictionary<string, object> Metadata { get; set; } = new();
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public bool IsActive { get; set; }
    }

    private class Person
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    private class RootObject
    {
        public string Name { get; set; } = "";
        public ChildObject? Child { get; set; }
    }

    private class ChildObject
    {
        public int Value { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public string[] Tags { get; set; } = Array.Empty<string>();
        public NestedObject? Nested { get; set; }
    }

    private class NestedObject
    {
        public string Secret { get; set; } = "";
        public DateTime Timestamp { get; set; }
    }

    private class ListContainer
    {
        public List<int> Numbers { get; set; } = new();
        public List<string> Names { get; set; } = new();
        public Dictionary<string, int> Dictionary { get; set; } = new();
    }

    private class ArrayContainer
    {
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
        public object[] Objects { get; set; } = Array.Empty<object>();
    }

    private class SimpleObject
    {
        public string Name { get; set; } = "";
        public int Value { get; set; }
    }
}