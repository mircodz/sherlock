using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Sherlock.Core.Diagnostics;
using Sherlock.Core.Tests.Common;
using Sherlock.Heap;
using Sherlock.Heap.Analysis;
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

        AssertCancelled(token, () => snapshot.Heap.GetDominatorTree(token));
        AssertCancelled(token, () => snapshot.Heap.GetHistogram(token));
        AssertCancelled(token, () => snapshot.Heap.GetExceptions(token));
        AssertCancelled(token, () => snapshot.Heap.DuplicateStrings(cancellationToken: token));
        AssertCancelled(token, () => snapshot.Heap.Finalizers(token));
        AssertCancelled(token, () => snapshot.Heap.EventHandlerLeaks(cancellationToken: token));
        AssertCancelled(token, () => snapshot.Diagnose(token));
    }

    [Fact]
    public void CachedAnalysisRejectsCancellationAndPreservesCachedResults()
    {
        var empty = new HeapGraph(Array.Empty<ulong>(), Array.Empty<uint>(), new int[2], []);
        DominatorTree dominators = GraphAnalysis.Dominators(empty);
        ExceptionInfo[] exceptions = [new(0x1000, "App.Error", "message", 0, null)];
        var finalizers = new FinalizerReport(0, 0, []);
        Finding[] diagnosis = [new(FindingSeverity.Info, "growth", "title", "detail")];
        Snapshot snapshot = WithoutRuntime(
            ("_dominators", new Once<DominatorTree>(dominators)),
            ("_exceptions", new Once<IReadOnlyList<ExceptionInfo>>(exceptions)),
            ("_finalizers", new Once<FinalizerReport>(finalizers)),
            ("_diagnosis", new Once<IReadOnlyList<Finding>>(diagnosis)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        CancellationToken token = cancellation.Token;

        AssertCancelled(token, () => snapshot.Heap.GetDominatorTree(token));
        AssertCancelled(token, () => snapshot.Heap.GetExceptions(token));
        AssertCancelled(token, () => snapshot.Heap.Finalizers(token));
        AssertCancelled(token, () => snapshot.Diagnose(token));
        Assert.Same(dominators, snapshot.Heap.GetDominatorTree());
        Assert.Same(dominators, snapshot.Heap.Dominators);
        Assert.Same(exceptions, snapshot.Heap.GetExceptions());
        Assert.Same(exceptions, snapshot.Heap.Exceptions);
        Assert.Same(finalizers, snapshot.Heap.Finalizers());
        Assert.Same(diagnosis, snapshot.Diagnose());
    }

    private static void AssertCancelled(CancellationToken token, Action analysis)
    {
        var error = Assert.Throws<OperationCanceledException>(analysis);
        Assert.Equal(token, error.CancellationToken);
    }

    // No runtime resources: cached and pre-cancelled queries must never dereference them. Each cache goes to whichever
    // of the snapshot and its heap declares it.
    private static Snapshot WithoutRuntime(params (string Field, object Value)[] caches)
    {
        var heap = (HeapDump)RuntimeHelpers.GetUninitializedObject(typeof(HeapDump));
        var snapshot = (Snapshot)RuntimeHelpers.GetUninitializedObject(typeof(Snapshot));
        Set(snapshot, $"<{nameof(Snapshot.Heap)}>k__BackingField", heap);
        foreach ((string field, object value) in caches)
        {
            object owner = typeof(HeapDump).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic) is null ? snapshot : heap;
            Set(owner, field, value);
        }
        return snapshot;
    }

    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
