using NUnit.Framework;
using Sherlock.Testing;

namespace Sherlock.Tests;

/// <summary>
/// Tests demonstrating detection of memory leaks caused by timers and background tasks.
/// Timers and CancellationTokenSource objects are common sources of leaks when not properly disposed.
/// </summary>
[TestFixture]
public class TimerLeakTests
{
    [TearDown]
    public void TearDown()
    {
        // Clean up any remaining timers and tasks
        BackgroundTaskManager.StopAll();
        TestUtilities.ForceGarbageCollection();
    }

    [Test]
    public async Task DetectSystemTimerLeak()
    {
        // Arrange: Create System.Threading.Timer instances without disposing
        var timerHolders = new List<TimerHolder>();
        
        for (int i = 0; i < 25; i++)
        {
            var holder = new TimerHolder($"Timer-{i}", TimeSpan.FromSeconds(1));
            timerHolders.Add(holder);
            // NOT disposing the timers
        }

        // Clear local references
        timerHolders.Clear();
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Timer holders should still be alive due to timer callbacks
        snapshot.Types().Where(typeof(TimerHolder)).Instances.Should().BeGreaterOrEqualTo(20);

        // Cleanup
        BackgroundTaskManager.StopAll();
    }

    [Test]
    public async Task DetectCancellationTokenSourceLeak()
    {
        // Arrange: Create CancellationTokenSource instances without disposing
        var ctsHolders = new List<CancellationTokenSourceHolder>();
        
        for (int i = 0; i < 30; i++)
        {
            var holder = new CancellationTokenSourceHolder($"CTS-{i}");
            ctsHolders.Add(holder);
            // NOT disposing the CancellationTokenSource
        }

        // Clear local references
        ctsHolders.Clear();
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: CTS holders should still be alive
        snapshot.Types().Where(typeof(CancellationTokenSourceHolder)).Instances.Should().BeGreaterOrEqualTo(25);
    }

    [Test]
    public async Task DetectBackgroundTaskLeak()
    {
        // Arrange: Start background tasks that capture objects
        var taskHolders = new List<TaskHolder>();
        
        for (int i = 0; i < 20; i++)
        {
            var holder = new TaskHolder($"Task-{i}");
            BackgroundTaskManager.StartTask(holder);
            taskHolders.Add(holder);
        }

        // Clear local references but tasks are still running
        taskHolders.Clear();
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Task holders should still be alive due to running tasks
        snapshot.Types().Where(typeof(TaskHolder)).Instances.Should().BeGreaterOrEqualTo(18);

        // Cleanup
        BackgroundTaskManager.StopAll();
    }

    [Test]
    public async Task DetectPeriodicTimerLeak()
    {
        // Arrange: Create PeriodicTimer instances (NET 6+)
        var periodicTimerHolders = new List<PeriodicTimerHolder>();
        
        for (int i = 0; i < 15; i++)
        {
            var holder = new PeriodicTimerHolder($"PeriodicTimer-{i}", TimeSpan.FromMilliseconds(500));
            periodicTimerHolders.Add(holder);
            // NOT disposing the periodic timers
        }

        // Clear local references
        periodicTimerHolders.Clear();
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Periodic timer holders should still be alive
        snapshot.Types().Where(typeof(PeriodicTimerHolder)).Instances.Should().BeGreaterOrEqualTo(12);
    }

    [Test]
    public async Task DetectAsyncMethodStateMachineLeak()
    {
        // Arrange: Start async methods that don't complete
        var asyncHolders = new List<AsyncOperationHolder>();
        
        for (int i = 0; i < 20; i++)
        {
            var holder = new AsyncOperationHolder($"Async-{i}");
            _ = holder.StartLongRunningOperation(); // Fire and forget - potential leak
            asyncHolders.Add(holder);
        }

        // Clear local references
        asyncHolders.Clear();
        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Async holders should still be alive due to pending operations
        snapshot.Types().Where(typeof(AsyncOperationHolder)).Instances.Should().BeGreaterOrEqualTo(18);

        // Cleanup
        BackgroundTaskManager.StopAll();
    }

    [Test]
    public async Task DetectProperTimerDisposalWorks()
    {
        // Arrange: Create and properly dispose timers
        CreateAndDisposeTimers();

        TestUtilities.ForceGarbageCollection();

        // Act: Take snapshot
        var snapshot = await TestUtilities.TakeAnalyzedSnapshotAsync();

        // Assert: Disposed timer holders should be collectable
        var disposedTimerCount = snapshot.Types().Where(typeof(DisposableTimerHolder)).Instances;
        Assert.That(disposedTimerCount, Is.LessThan(5), "Expected disposed timer holders to be collected");
    }

    // Helper methods
    private static void CreateAndDisposeTimers()
    {
        var timers = new List<DisposableTimerHolder>();
        for (int i = 0; i < 20; i++)
        {
            var timer = new DisposableTimerHolder($"Disposable-{i}");
            timers.Add(timer);
            timer.Dispose(); // Properly dispose
        }
        timers.Clear();
    }
}

// Test infrastructure classes

/// <summary>
/// Manages background tasks for testing.
/// </summary>
public static class BackgroundTaskManager
{
    private static readonly List<CancellationTokenSource> _cancellationTokens = new();
    private static readonly List<Timer> _timers = new();

    public static void StartTask(TaskHolder holder)
    {
        var cts = new CancellationTokenSource();
        _cancellationTokens.Add(cts);
        
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    holder.DoWork();
                    await Task.Delay(100, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
        }, cts.Token);
    }

    public static void StopAll()
    {
        foreach (var cts in _cancellationTokens)
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch { }
        }
        _cancellationTokens.Clear();

        foreach (var timer in _timers)
        {
            try
            {
                timer.Dispose();
            }
            catch { }
        }
        _timers.Clear();
    }

    public static void RegisterTimer(Timer timer)
    {
        _timers.Add(timer);
    }

    public static void RegisterCancellationToken(CancellationTokenSource cts)
    {
        _cancellationTokens.Add(cts);
    }
}

// Test classes
public class TimerHolder
{
    public string Name { get; }
    public byte[] Data { get; }
    private readonly Timer _timer;

    public TimerHolder(string name, TimeSpan interval)
    {
        Name = name;
        Data = new byte[1024];
        
        _timer = new Timer(TimerCallback, this, interval, interval);
        BackgroundTaskManager.RegisterTimer(_timer);
    }

    private static void TimerCallback(object? state)
    {
        if (state is TimerHolder holder)
        {
            // Timer callback keeps holder alive
            holder.DoWork();
        }
    }

    private void DoWork()
    {
        // Simulate work that keeps object alive
        var temp = Data.Sum(b => (int)b);
    }
}

public class CancellationTokenSourceHolder
{
    public string Name { get; }
    public byte[] Data { get; }
    public CancellationTokenSource TokenSource { get; }

    public CancellationTokenSourceHolder(string name)
    {
        Name = name;
        Data = new byte[512];
        TokenSource = new CancellationTokenSource();
        
        // Start a long-running task that keeps the holder alive (simulating a leak)
        var longRunningTask = Task.Run(async () =>
        {
            while (!TokenSource.Token.IsCancellationRequested)
            {
                await Task.Delay(100, TokenSource.Token);
                // Do work that references this holder, keeping it alive
                DoWork();
            }
        }, TokenSource.Token);
        
        // Register with BackgroundTaskManager so it can be cleaned up
        BackgroundTaskManager.RegisterCancellationToken(TokenSource);
    }

    private void DoWork()
    {
        // Simulate work that keeps the holder alive
        var temp = Data.Sum(b => (int)b);
    }
}

public class TaskHolder
{
    public string Name { get; }
    public byte[] Buffer { get; }

    public TaskHolder(string name)
    {
        Name = name;
        Buffer = new byte[768];
    }

    public void DoWork()
    {
        // Simulate work
        var checksum = Buffer.Aggregate(0, (acc, b) => acc + b);
    }
}

public class PeriodicTimerHolder
{
    public string Name { get; }
    public byte[] Data { get; }
    public PeriodicTimer Timer { get; }

    public PeriodicTimerHolder(string name, TimeSpan period)
    {
        Name = name;
        Data = new byte[1024];
        Timer = new PeriodicTimer(period);
        
        // Start the periodic operation
        _ = RunPeriodicOperation();
    }

    private async Task RunPeriodicOperation()
    {
        try
        {
            while (await Timer.WaitForNextTickAsync())
            {
                DoWork();
            }
        }
        catch (ObjectDisposedException)
        {
            // Timer was disposed
        }
    }

    private void DoWork()
    {
        // Work that keeps the holder alive
        var sum = Data.Sum(b => (int)b);
    }
}

public class AsyncOperationHolder
{
    public string Name { get; }
    public byte[] Data { get; }

    public AsyncOperationHolder(string name)
    {
        Name = name;
        Data = new byte[2048];
    }

    public async Task StartLongRunningOperation()
    {
        try
        {
            // Simulate long-running async operation
            while (true)
            {
                await Task.Delay(1000);
                DoWork();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when operation is cancelled
        }
    }

    private void DoWork()
    {
        // Work that keeps holder alive
        var result = Data.Where(b => b > 128).Count();
    }
}

public class DisposableTimerHolder : IDisposable
{
    public string Name { get; }
    public byte[] Data { get; }
    private readonly Timer _timer;
    private bool _disposed;

    public DisposableTimerHolder(string name)
    {
        Name = name;
        Data = new byte[512];
        _timer = new Timer(TimerCallback, this, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private static void TimerCallback(object? state)
    {
        // Timer callback
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _timer?.Dispose();
            _disposed = true;
        }
    }
}