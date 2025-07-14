using NUnit.Framework;
using Sherlock.Testing;

namespace Sherlock.Tests;

/// <summary>
/// Tests demonstrating detection of memory leaks caused by event handler subscriptions.
/// Event handlers are a common source of memory leaks when objects subscribe to events
/// but forget to unsubscribe, keeping the subscriber alive.
/// </summary>
[TestFixture]
public class EventHandlerLeakTests
{
    [TearDown]
    public void TearDown()
    {
        // Clean up any remaining event subscriptions
        EventPublisher.ClearAllEvents();
        TestUtilities.ForceGarbageCollection();
    }

    [Test]
    public async Task DetectEventHandlerLeak()
    {
        // Arrange: Create event publisher and subscribers
        var publisher = new EventPublisher();
        var subscribers = new List<EventSubscriber>();

        // Subscribe multiple objects to the event
        for (int i = 0; i < 30; i++)
        {
            var subscriber = new EventSubscriber($"Subscriber-{i}");
            publisher.SomeEvent += subscriber.HandleEvent;
            subscribers.Add(subscriber);
        }

        // Clear local references to subscribers (but they're still referenced by event)
        subscribers.Clear();
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Subscribers should still be alive due to event references
        snapshot.Types().Where(typeof(EventSubscriber)).Instances.Should().BeGreaterOrEqualTo(25);

        // Demonstrate that proper cleanup works
        publisher.ClearEvent();
        TestUtilities.ForceGarbageCollection();
        
        var cleanSnapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();
        var remainingSubscribers = cleanSnapshot.Types().Where(typeof(EventSubscriber)).Instances;
        Assert.That(remainingSubscribers, Is.LessThan(5), "Expected subscribers to be collected after event cleanup");
    }

    [Test]
    public async Task DetectWeakEventPatternWorks()
    {
        // Arrange: Create publisher with weak event pattern
        var publisher = new WeakEventPublisher();
        
        CreateAndSubscribeWeakEventHandlers(publisher);
        
        // Force GC - weak references should allow collection
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: WeakEventSubscribers should be collected due to weak references
        var weakSubscriberCount = snapshot.Types().Where(typeof(WeakEventSubscriber)).Instances;
        Assert.That(weakSubscriberCount, Is.LessThan(5), "Expected weak event subscribers to be collected");

        // Cleanup
        publisher.Dispose();
    }

    [Test]
    public async Task DetectLambdaClosureLeak()
    {
        // Arrange: Create closures that capture large objects
        var publisher = new EventPublisher();
        var capturedObjects = new List<LargeObject>();

        // Create lambda closures that capture objects
        for (int i = 0; i < 20; i++)
        {
            var largeObj = new LargeObject($"Large-{i}");
            capturedObjects.Add(largeObj);
            
            // Lambda captures largeObj, creating a closure
            publisher.SomeEvent += (data) => 
            {
                // This lambda captures largeObj, preventing its collection
                Console.WriteLine($"Processing {data} with {largeObj.Name}");
            };
        }

        // Clear local references
        capturedObjects.Clear();
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Large objects should still be alive due to closure capture
        snapshot.Types().Where(typeof(LargeObject)).Instances.Should().BeGreaterOrEqualTo(18);

        // Cleanup
        publisher.ClearEvent();
    }

    [Test]
    public async Task DetectProperEventUnsubscriptionWorks()
    {
        // Arrange: Create publisher and subscribers with proper cleanup
        var publisher = new EventPublisher();
        var properSubscribers = CreateAndSubscribeProperEventHandlers(publisher);

        // Properly unsubscribe all handlers
        foreach (var subscriber in properSubscribers)
        {
            publisher.SomeEvent -= subscriber.HandleEvent;
        }
        
        properSubscribers.Clear();
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: ProperEventSubscribers should be collected after unsubscription
        var properSubscriberCount = snapshot.Types().Where(typeof(ProperEventSubscriber)).Instances;
        Assert.That(properSubscriberCount, Is.LessThan(3), "Expected proper subscribers to be collected after unsubscription");
    }

    [Test]
    public async Task DetectAnonymousMethodLeak()
    {
        // Arrange: Subscribe using anonymous methods that capture state
        var publisher = new EventPublisher();
        var stateObjects = new List<StateObject>();

        for (int i = 0; i < 15; i++)
        {
            var state = new StateObject(i);
            stateObjects.Add(state);

            // Anonymous method captures state
            publisher.SomeEvent += delegate(string data)
            {
                state.Process(data); // Captures state object
            };
        }

        // Clear local state references
        stateObjects.Clear();
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: State objects should still be alive due to anonymous method capture
        snapshot.Types().Where(typeof(StateObject)).Instances.Should().BeGreaterOrEqualTo(12);

        // Cleanup
        publisher.ClearEvent();
    }

    // Helper methods
    private static void CreateAndSubscribeWeakEventHandlers(WeakEventPublisher publisher)
    {
        for (int i = 0; i < 25; i++)
        {
            var subscriber = new WeakEventSubscriber($"WeakSubscriber-{i}");
            publisher.Subscribe(subscriber);
        }
        // Subscribers go out of scope here
    }

    private static List<ProperEventSubscriber> CreateAndSubscribeProperEventHandlers(EventPublisher publisher)
    {
        var subscribers = new List<ProperEventSubscriber>();
        for (int i = 0; i < 20; i++)
        {
            var subscriber = new ProperEventSubscriber($"ProperSubscriber-{i}");
            publisher.SomeEvent += subscriber.HandleEvent;
            subscribers.Add(subscriber);
        }
        return subscribers;
    }
}

// Test infrastructure classes

/// <summary>
/// Standard event publisher that holds strong references to subscribers.
/// </summary>
public class EventPublisher
{
    public event Action<string>? SomeEvent;

    public void PublishEvent(string data)
    {
        SomeEvent?.Invoke(data);
    }

    public void ClearEvent()
    {
        SomeEvent = null;
    }

    public static void ClearAllEvents()
    {
        // Helper for cleanup in tests
    }
}

/// <summary>
/// Event publisher using weak references to avoid memory leaks.
/// </summary>
public class WeakEventPublisher : IDisposable
{
    private readonly List<WeakReference<WeakEventSubscriber>> _subscribers = new();

    public void Subscribe(WeakEventSubscriber subscriber)
    {
        _subscribers.Add(new WeakReference<WeakEventSubscriber>(subscriber));
    }

    public void PublishEvent(string data)
    {
        var deadReferences = new List<WeakReference<WeakEventSubscriber>>();
        
        foreach (var weakRef in _subscribers)
        {
            if (weakRef.TryGetTarget(out var subscriber))
            {
                subscriber.HandleEvent(data);
            }
            else
            {
                deadReferences.Add(weakRef);
            }
        }

        // Clean up dead references
        foreach (var deadRef in deadReferences)
        {
            _subscribers.Remove(deadRef);
        }
    }

    public void Dispose()
    {
        _subscribers.Clear();
    }
}

// Test subscriber classes
public class EventSubscriber
{
    public string Name { get; }
    public byte[] Buffer { get; }

    public EventSubscriber(string name)
    {
        Name = name;
        Buffer = new byte[1024];
    }

    public void HandleEvent(string data)
    {
        // Simulate event handling
        var processed = $"{Name}: {data}";
    }
}

public class WeakEventSubscriber
{
    public string Name { get; }
    public byte[] Data { get; }

    public WeakEventSubscriber(string name)
    {
        Name = name;
        Data = new byte[512];
    }

    public void HandleEvent(string data)
    {
        // Handle weak event
    }
}

public class ProperEventSubscriber
{
    public string Name { get; }
    public byte[] Buffer { get; }

    public ProperEventSubscriber(string name)
    {
        Name = name;
        Buffer = new byte[768];
    }

    public void HandleEvent(string data)
    {
        // Proper event handling with cleanup
    }
}

public class LargeObject
{
    public string Name { get; }
    public byte[] LargeData { get; }

    public LargeObject(string name)
    {
        Name = name;
        LargeData = new byte[4096]; // Large object for visible leaks
    }
}

public class StateObject
{
    public int Id { get; }
    public byte[] State { get; }

    public StateObject(int id)
    {
        Id = id;
        State = new byte[1024];
    }

    public void Process(string data)
    {
        // Simulate state processing
    }
}