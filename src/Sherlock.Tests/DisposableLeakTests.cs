using NUnit.Framework;
using Sherlock.Testing;

namespace Sherlock.Tests;

/// <summary>
/// Tests demonstrating detection of memory leaks caused by IDisposable objects
/// that are not properly disposed. While the GC will eventually collect these objects,
/// they may hold onto unmanaged resources and cause resource leaks.
/// </summary>
[TestFixture]
public class DisposableLeakTests
{
    [TearDown]
    public void TearDown()
    {
        // Clean up any test resources
        DisposableTracker.ClearAll();
        TestUtilities.ForceGarbageCollection();
    }

    [Test]
    public async Task DetectUndisposedObjects()
    {
        // Arrange: Create disposable objects without disposing them
        var undisposedObjects = new List<LeakyDisposable>();
        for (int i = 0; i < 40; i++)
        {
            var disposable = new LeakyDisposable($"Undisposed-{i}");
            undisposedObjects.Add(disposable);
            // NOT calling Dispose() - this simulates a leak
        }

        // Clear local references
        undisposedObjects.Clear();
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Objects should still exist and show as undisposed
        snapshot.Types().Where(typeof(LeakyDisposable)).Instances.Should().BeGreaterOrEqualTo(35);

        // Check that they're actually undisposed
        var undisposedCount = DisposableTracker.GetUndisposedCount();
        Assert.That(undisposedCount, Is.GreaterThanOrEqualTo(35), "Expected undisposed objects to be tracked");
    }

    [Test]
    public async Task DetectProperDisposalWorks()
    {
        // Arrange: Create disposable objects and properly dispose them
        var disposedObjects = CreateAndDisposeObjects();

        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Objects may still exist in memory but should be marked as disposed
        var disposedCount = DisposableTracker.GetDisposedCount();
        Assert.That(disposedCount, Is.GreaterThanOrEqualTo(20), "Expected objects to be properly disposed");

        var undisposedCount = DisposableTracker.GetUndisposedCount();
        Assert.That(undisposedCount, Is.EqualTo(0), "Expected no undisposed objects after proper cleanup");
    }

    [Test]
    public async Task DetectUsingStatementWorks()
    {
        // Arrange: Use 'using' statements for automatic disposal
        CreateObjectsWithUsingStatement();

        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: All objects created with 'using' should be disposed
        var undisposedCount = DisposableTracker.GetUndisposedCount();
        Assert.That(undisposedCount, Is.EqualTo(0), "Expected all objects created with 'using' to be disposed");
    }

    [Test]
    public async Task DetectFileStreamLeak()
    {
        // Arrange: Create file streams without disposing (simulated)
        var streamWrappers = new List<FileStreamWrapper>();
        
        for (int i = 0; i < 25; i++)
        {
            var wrapper = new FileStreamWrapper($"fake-file-{i}.txt");
            streamWrappers.Add(wrapper);
            // NOT disposing the wrapper (simulates file handle leak)
        }

        streamWrappers.Clear();
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: File stream wrappers should be in memory and undisposed
        snapshot.Types().Where(typeof(FileStreamWrapper)).Instances.Should().BeGreaterOrEqualTo(20);
        
        var undisposedFileStreams = DisposableTracker.GetUndisposedOfType<FileStreamWrapper>();
        Assert.That(undisposedFileStreams, Is.GreaterThanOrEqualTo(20), "Expected undisposed file stream wrappers");
    }

    [Test]
    public async Task DetectTimerLeak()
    {
        // Arrange: Create timers without disposing them
        var timerWrappers = new List<TimerWrapper>();
        
        for (int i = 0; i < 15; i++)
        {
            var timer = new TimerWrapper(TimeSpan.FromSeconds(1));
            timerWrappers.Add(timer);
            // NOT disposing timers
        }

        timerWrappers.Clear();
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Timer wrappers should still be alive and undisposed
        snapshot.Types().Where(typeof(TimerWrapper)).Instances.Should().BeGreaterOrEqualTo(12);
        
        var undisposedTimers = DisposableTracker.GetUndisposedOfType<TimerWrapper>();
        Assert.That(undisposedTimers, Is.GreaterThanOrEqualTo(12), "Expected undisposed timer wrappers");
    }

    [Test]
    public async Task DetectCancellationTokenSourceLeak()
    {
        // Arrange: Create CancellationTokenSource instances without disposing
        var ctsWrappers = new List<CancellationTokenSourceWrapper>();
        
        for (int i = 0; i < 30; i++)
        {
            var cts = new CancellationTokenSourceWrapper();
            ctsWrappers.Add(cts);
            // NOT disposing CTS instances
        }

        ctsWrappers.Clear();
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: CTS wrappers should be alive and undisposed
        snapshot.Types().Where(typeof(CancellationTokenSourceWrapper)).Instances.Should().BeGreaterOrEqualTo(25);
        
        var undisposedCts = DisposableTracker.GetUndisposedOfType<CancellationTokenSourceWrapper>();
        Assert.That(undisposedCts, Is.GreaterThanOrEqualTo(25), "Expected undisposed CancellationTokenSource wrappers");
    }

    // Helper methods
    private static List<ProperDisposable> CreateAndDisposeObjects()
    {
        var objects = new List<ProperDisposable>();
        for (int i = 0; i < 25; i++)
        {
            var disposable = new ProperDisposable($"Proper-{i}");
            objects.Add(disposable);
            disposable.Dispose(); // Properly dispose
        }
        return objects;
    }

    private static void CreateObjectsWithUsingStatement()
    {
        for (int i = 0; i < 20; i++)
        {
            using var disposable = new UsingDisposable($"Using-{i}");
            // Automatically disposed at end of scope
        }
    }
}

// Test infrastructure classes

/// <summary>
/// Tracks disposable objects for testing purposes.
/// </summary>
public static class DisposableTracker
{
    // Use strong references for undisposed objects to simulate memory leaks
    private static readonly List<IDisposable> _undisposed = new();
    // Use weak references for disposed objects (they can be collected)
    private static readonly List<WeakReference> _disposed = new();
    private static readonly object _lock = new();

    public static void TrackUndisposed(IDisposable obj)
    {
        lock (_lock)
        {
            _undisposed.Add(obj);
        }
    }

    public static void TrackDisposed(IDisposable obj)
    {
        lock (_lock)
        {
            // Move from undisposed to disposed
            _undisposed.Remove(obj);
            _disposed.Add(new WeakReference(obj));
        }
    }

    public static int GetUndisposedCount()
    {
        lock (_lock)
        {
            CleanupDeadReferences();
            return _undisposed.Count;
        }
    }

    public static int GetDisposedCount()
    {
        lock (_lock)
        {
            CleanupDeadReferences();
            return _disposed.Count(wr => wr.IsAlive);
        }
    }

    public static int GetUndisposedOfType<T>()
    {
        lock (_lock)
        {
            CleanupDeadReferences();
            return _undisposed.Count(obj => obj is T);
        }
    }

    public static void ClearAll()
    {
        lock (_lock)
        {
            _undisposed.Clear();
            _disposed.Clear();
        }
    }

    private static void CleanupDeadReferences()
    {
        // Only clean up disposed weak references (undisposed are strong references)
        for (int i = _disposed.Count - 1; i >= 0; i--)
        {
            if (!_disposed[i].IsAlive)
                _disposed.RemoveAt(i);
        }
    }
}

// Test disposable classes
public class LeakyDisposable : IDisposable
{
    public string Name { get; }
    public byte[] Data { get; }
    private bool _disposed;

    public LeakyDisposable(string name)
    {
        Name = name;
        Data = new byte[1024];
        DisposableTracker.TrackUndisposed(this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            DisposableTracker.TrackDisposed(this);
            _disposed = true;
        }
    }
}

public class ProperDisposable : IDisposable
{
    public string Name { get; }
    public byte[] Data { get; }
    private bool _disposed;

    public ProperDisposable(string name)
    {
        Name = name;
        Data = new byte[512];
        DisposableTracker.TrackUndisposed(this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            DisposableTracker.TrackDisposed(this);
            _disposed = true;
        }
    }
}

public class UsingDisposable : IDisposable
{
    public string Name { get; }
    public byte[] Data { get; }
    private bool _disposed;

    public UsingDisposable(string name)
    {
        Name = name;
        Data = new byte[256];
        DisposableTracker.TrackUndisposed(this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            DisposableTracker.TrackDisposed(this);
            _disposed = true;
        }
    }
}

public class FileStreamWrapper : IDisposable
{
    public string FileName { get; }
    public byte[] Buffer { get; }
    private bool _disposed;

    public FileStreamWrapper(string fileName)
    {
        FileName = fileName;
        Buffer = new byte[2048]; // Simulate file buffer
        DisposableTracker.TrackUndisposed(this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            DisposableTracker.TrackDisposed(this);
            _disposed = true;
        }
    }
}

public class TimerWrapper : IDisposable
{
    public TimeSpan Interval { get; }
    public byte[] State { get; }
    private bool _disposed;

    public TimerWrapper(TimeSpan interval)
    {
        Interval = interval;
        State = new byte[512];
        DisposableTracker.TrackUndisposed(this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            DisposableTracker.TrackDisposed(this);
            _disposed = true;
        }
    }
}

public class CancellationTokenSourceWrapper : IDisposable
{
    public byte[] Data { get; }
    private bool _disposed;

    public CancellationTokenSourceWrapper()
    {
        Data = new byte[256];
        DisposableTracker.TrackUndisposed(this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            DisposableTracker.TrackDisposed(this);
            _disposed = true;
        }
    }
}