using System;
using System.Threading;
using Sherlock.Heap;
using Xunit;

namespace Sherlock.Core.Tests.Common;

public sealed class OnceTests
{
    [Fact]
    public void ConcurrentCallersShareOneComputation()
    {
        var once = new Once<object>();
        int computations = 0;
        object?[] results = RunTogether(16, () => once.Get(() =>
        {
            Interlocked.Increment(ref computations);
            Thread.Sleep(50);
            return new object();
        }));

        Assert.Equal(1, computations);
        Assert.All(results, result => Assert.Same(results[0], result));
    }

    [Fact]
    public void FailedComputationIsRetriedByTheNextCaller()
    {
        var once = new Once<string>();

        Assert.Throws<InvalidOperationException>(() => once.Get(() => throw new InvalidOperationException()));

        Assert.Equal("value", once.Get(() => "value"));
        Assert.Equal("value", once.Get(() => throw new InvalidOperationException("cached values are not recomputed")));
    }

    [Fact]
    public void CancellingAWaitingCallerLeavesTheComputationRunning()
    {
        var once = new Once<string>();
        using var computing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        string? computed = null;
        var first = new Thread(() => computed = once.Get(() =>
        {
            computing.Set();
            release.Wait();
            return "value";
        }));
        first.Start();
        computing.Wait();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => once.Get(() => "other", cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);

        release.Set();
        first.Join();
        Assert.Equal("value", computed);
        Assert.Equal("value", once.Get(() => "other"));
    }

    // Starts every call at the same moment on its own thread.
    internal static T?[] RunTogether<T>(int count, Func<T> call)
    {
        var results = new T?[count];
        using var start = new Barrier(count);
        var threads = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            int slot = i;
            threads[i] = new Thread(() =>
            {
                start.SignalAndWait();
                results[slot] = call();
            });
            threads[i].Start();
        }
        foreach (Thread thread in threads)
        {
            thread.Join();
        }
        return results;
    }
}
