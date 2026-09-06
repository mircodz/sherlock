using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Sherlock.Core.Analysis;
using Sherlock.Core.Diagnostics;
using Xunit;

namespace Sherlock.Core.Tests.Analysis;

public sealed class SnapshotCancellationTests
{
    [Fact]
    public void AnalysisRejectsPreCancellationBeforeAccessingTheRuntime()
    {
        Snapshot snapshot = WithoutRuntime();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        CancellationToken token = cancellation.Token;

        AssertCancelled(token, () => snapshot.GetDominatorTree(token));
        AssertCancelled(token, () => snapshot.GetExceptions(token));
        AssertCancelled(token, () => snapshot.DuplicateStrings(cancellationToken: token));
        AssertCancelled(token, () => snapshot.Finalizers(token));
        AssertCancelled(token, () => snapshot.EventHandlerLeaks(cancellationToken: token));
        AssertCancelled(token, () => snapshot.Diagnose(token));
    }

    [Fact]
    public void CachedAnalysisRejectsCancellationAndPreservesCachedResults()
    {
        var dominators = new DominatorTree(null!, [0], [0], [0], [0], []);
        ExceptionInfo[] exceptions = [new(0x1000, "App.Error", "message", 0, null)];
        var finalizers = new FinalizerReport(0, 0, []);
        Finding[] diagnosis = [new(FindingSeverity.Info, "growth", "title", "detail")];
        Snapshot snapshot = WithoutRuntime(
            ("_dominators", dominators),
            ("_exceptions", exceptions),
            ("_finalizers", finalizers),
            ("_diagnosis", diagnosis));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        CancellationToken token = cancellation.Token;

        AssertCancelled(token, () => snapshot.GetDominatorTree(token));
        AssertCancelled(token, () => snapshot.GetExceptions(token));
        AssertCancelled(token, () => snapshot.Finalizers(token));
        AssertCancelled(token, () => snapshot.Diagnose(token));
        Assert.Same(dominators, snapshot.GetDominatorTree());
        Assert.Same(dominators, snapshot.Dominators);
        Assert.Same(exceptions, snapshot.GetExceptions());
        Assert.Same(exceptions, snapshot.Exceptions);
        Assert.Same(finalizers, snapshot.Finalizers());
        Assert.Same(diagnosis, snapshot.Diagnose());
    }

    private static void AssertCancelled(CancellationToken token, Action analysis)
    {
        var error = Assert.Throws<OperationCanceledException>(analysis);
        Assert.Equal(token, error.CancellationToken);
    }

    // No runtime resources: cached and pre-cancelled queries must never dereference them.
    private static Snapshot WithoutRuntime(params (string Field, object Value)[] caches)
    {
        var snapshot = (Snapshot)RuntimeHelpers.GetUninitializedObject(typeof(Snapshot));
        foreach ((string field, object value) in caches)
        {
            typeof(Snapshot).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(snapshot, value);
        }
        return snapshot;
    }
}
