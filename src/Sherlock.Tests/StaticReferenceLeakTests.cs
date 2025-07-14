using NUnit.Framework;
using Sherlock.Testing;

namespace Sherlock.Tests;

/// <summary>
/// Tests demonstrating detection of memory leaks caused by static references.
/// Static references are one of the most common causes of memory leaks in .NET applications.
/// </summary>
[TestFixture]
public class StaticReferenceLeakTests
{
    [SetUp]
    public void Setup()
    {
        // Clear any existing static references before each test
        GlobalCache.Clear();
        StaticEventManager.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        // Clean up after each test
        GlobalCache.Clear();
        StaticEventManager.Clear();
        TestUtilities.ForceGarbageCollection();
    }

    [Test]
    public async Task DetectStaticCollectionLeak()
    {
        // Arrange: Add objects to static collection (this creates a leak!)
        var leakedObjects = new List<LeakedObject>();
        for (int i = 0; i < 100; i++)
        {
            var obj = new LeakedObject($"Leaked-{i}");
            GlobalCache.AddObject(obj);
            leakedObjects.Add(obj); // Keep local reference for verification
        }

        // Clear local references but static cache still holds them
        leakedObjects.Clear();
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Objects should still be alive due to static reference
        snapshot.Types().Where(typeof(LeakedObject)).Instances.Should().BeGreaterOrEqualTo(90);

        // Demonstrate the leak is real - clear static cache and objects should be collected
        GlobalCache.Clear();
        TestUtilities.ForceGarbageCollection();
        
        var cleanSnapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();
        var remainingCount = cleanSnapshot.Types().Where(typeof(LeakedObject)).Instances;
        Assert.That(remainingCount, Is.LessThan(10), $"Expected most LeakedObjects to be collected after clearing static cache, but {remainingCount} remain");
    }

    [Test]
    public async Task DetectStaticEventHandlerLeak()
    {
        // Arrange: Subscribe to static event (creates leak through event handler)
        var subscribers = new List<StaticEventSubscriber>();
        
        for (int i = 0; i < 50; i++)
        {
            var subscriber = new StaticEventSubscriber($"Subscriber-{i}");
            StaticEventManager.Subscribe(subscriber.HandleEvent);
            subscribers.Add(subscriber);
        }

        // Clear local references - but static event still holds delegates pointing to objects
        subscribers.Clear();
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Subscribers should still be alive due to event handler references
        snapshot.Types().Where(typeof(StaticEventSubscriber)).Instances.Should().BeGreaterOrEqualTo(40);

        // Demonstrate cleanup works
        StaticEventManager.Clear();
        TestUtilities.ForceGarbageCollection();
        
        var cleanSnapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();
        var remainingSubscribers = cleanSnapshot.Types().Where(typeof(StaticEventSubscriber)).Instances;
        Assert.That(remainingSubscribers, Is.LessThan(5), $"Expected StaticEventSubscribers to be collected after clearing static events");
    }

    [Test]
    public async Task DetectSingletonLeak()
    {
        // Arrange: Create objects held by singleton
        var dataObjects = new List<HeavyDataObject>();
        for (int i = 0; i < 20; i++)
        {
            var data = new HeavyDataObject(i);
            dataObjects.Add(data);
            LeakySingleton.Instance.AddData(data);
        }

        // Clear local references
        dataObjects.Clear();
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Data objects should be kept alive by singleton
        snapshot.Types().Where(typeof(HeavyDataObject)).Instances.Should().BeGreaterOrEqualTo(18);

        // Cleanup
        LeakySingleton.Instance.Clear();
    }

    [Test]
    public async Task DetectStaticDictionaryLeak()
    {
        // Arrange: Add objects to static dictionary with weak keys
        var keys = new List<string>();
        for (int i = 0; i < 75; i++)
        {
            var key = $"key-{i}";
            var value = new CachedValue($"Value for {key}", new byte[1024]);
            
            StaticDictionary.Add(key, value);
            keys.Add(key);
        }

        // Keep keys alive but values should only be referenced by static dictionary
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Cached values should be alive due to static dictionary
        snapshot.Types().Where(typeof(CachedValue)).Instances.Should().BeGreaterOrEqualTo(70);

        // Cleanup
        StaticDictionary.Clear();
        keys.Clear();
    }
}

// Test infrastructure classes demonstrating common leak patterns

/// <summary>
/// Simulates a global cache that holds references to objects (common leak source).
/// </summary>
public static class GlobalCache
{
    private static readonly List<object> _cache = new();

    public static void AddObject(object obj) => _cache.Add(obj);
    public static int Count => _cache.Count;
    public static void Clear() => _cache.Clear();
}

/// <summary>
/// Simulates static event management (common leak source via event handlers).
/// </summary>
public static class StaticEventManager
{
    public static event Action<string>? DataReceived;

    public static void Subscribe(Action<string> handler)
    {
        DataReceived += handler;
    }

    public static void RaiseEvent(string data)
    {
        DataReceived?.Invoke(data);
    }

    public static void Clear()
    {
        DataReceived = null;
    }
}

/// <summary>
/// Simulates a singleton holding data (common leak source).
/// </summary>
public class LeakySingleton
{
    private static readonly Lazy<LeakySingleton> _instance = new(() => new LeakySingleton());
    public static LeakySingleton Instance => _instance.Value;

    private readonly List<object> _data = new();

    private LeakySingleton() { }

    public void AddData(object data) => _data.Add(data);
    public int DataCount => _data.Count;
    public void Clear() => _data.Clear();
}

/// <summary>
/// Simulates a static dictionary cache.
/// </summary>
public static class StaticDictionary
{
    private static readonly Dictionary<string, object> _cache = new();

    public static void Add(string key, object value) => _cache[key] = value;
    public static int Count => _cache.Count;
    public static void Clear() => _cache.Clear();
}

// Test object classes
public class LeakedObject
{
    public string Name { get; }
    public byte[] Data { get; }

    public LeakedObject(string name)
    {
        Name = name;
        Data = new byte[512]; // Some data to make the leak more visible
    }
}

public class StaticEventSubscriber
{
    public string Name { get; }
    public byte[] Buffer { get; }

    public StaticEventSubscriber(string name)
    {
        Name = name;
        Buffer = new byte[256];
    }

    public void HandleEvent(string data)
    {
        // Event handler that could cause leaks if not unsubscribed
        Console.WriteLine($"{Name} received: {data}");
    }
}

public class HeavyDataObject
{
    public int Id { get; }
    public byte[] LargeData { get; }

    public HeavyDataObject(int id)
    {
        Id = id;
        LargeData = new byte[2048]; // Larger object to make leaks more visible
    }
}

public class CachedValue
{
    public string Value { get; }
    public byte[] Payload { get; }

    public CachedValue(string value, byte[] payload)
    {
        Value = value;
        Payload = payload;
    }
}