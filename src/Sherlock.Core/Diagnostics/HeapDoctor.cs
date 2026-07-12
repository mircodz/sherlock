using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Sherlock.Core.Analysis;

namespace Sherlock.Core.Diagnostics;

/// <summary>
/// Sweeps a snapshot through every inspector and reports the obvious problems - the engine behind
/// the <c>doctor</c> command. Each inspector is independent and failure-isolated, and each finding
/// carries the next command to run so you can drill in by hand.
/// </summary>
public sealed class HeapDoctor(DumpSession session)
{
    public IReadOnlyList<Finding> Diagnose(CancellationToken cancellation = default)
    {
        IReadOnlyList<HeapTypeStat> histogram = session.GetHistogram();
        long heapBytes = histogram.Sum(s => (long)s.TotalSize);

        var findings = new List<Finding>();
        Run(findings, () => Retention(findings, cancellation));
        Run(findings, () => EventHandlers(findings, cancellation));
        Run(findings, () => Finalizers(findings, cancellation));
        Run(findings, () => DuplicateStrings(findings, heapBytes));
        Run(findings, () => Fragmentation(findings, histogram, heapBytes));
        Run(findings, () => Growth(findings, histogram));

        return findings.OrderBy(f => f.Severity).ToList();
    }

    private static void Run(List<Finding> findings, Action inspector)
    {
        try
        {
            inspector();
        }
        catch (Exception ex)
        {
            findings.Add(new Finding(FindingSeverity.Info, "inspector-error", "An inspector failed to run", ex.Message));
        }
    }

    /// <summary>The single biggest retained graph - where memory concentrates and a leak hides.</summary>
    private void Retention(List<Finding> findings, CancellationToken cancellation)
    {
        DominatorTree tree = session.GetDominatorTree(cancellation);
        ulong total = tree.TotalReachableBytes;
        if (total == 0 || tree.TopDominators(1).FirstOrDefault() is not { } node)
        {
            return;
        }

        double pct = 100.0 * node.RetainedSize / total;
        if (pct < 10)
        {
            return;
        }

        string kind = IsCollection(node.TypeName) ? "collection " : "";
        findings.Add(new Finding(
            pct >= 50 ? FindingSeverity.High : FindingSeverity.Warning, "retention",
            $"{ShortType(node.TypeName)} {kind}retains {Bytes(node.RetainedSize)} ({pct:0}% of the reachable heap)",
            "The biggest retained graph - where a leak concentrates. See what holds it and what it holds.")
        {
            Address = node.Address,
            Type = node.TypeName,
            Bytes = (long)node.RetainedSize,
            NextCommand = $"gcroot 0x{node.Address:x}",
        });
    }

    /// <summary>A delegate with a large invocation list - the classic event-handler leak.</summary>
    private void EventHandlers(List<Finding> findings, CancellationToken cancellation)
    {
        if (new EventHandlerAnalyzer(session).Analyze(minSubscribers: 32, limit: 1, cancellation: cancellation).FirstOrDefault() is not { } worst)
        {
            return;
        }

        string targets = string.Join(", ", worst.Targets.Take(3).Select(t => $"{ShortType(t.TypeName)} x{t.Count}"));
        findings.Add(new Finding(
            worst.SubscriberCount >= 256 ? FindingSeverity.High : FindingSeverity.Warning, "event-handlers",
            $"An event has {worst.SubscriberCount:N0} subscribers - likely an event-handler leak",
            $"The {ShortType(worst.DelegateType)} delegate pins every subscriber until it unsubscribes (-=)."
            + (targets.Length > 0 ? $" Subscribers: {targets}." : ""))
        {
            Address = worst.DelegateAddress,
            Type = worst.DelegateType,
            Count = worst.SubscriberCount,
            NextCommand = "eventleaks",
        });
    }

    /// <summary>Objects still registered for finalization - a finalizer that wasn't suppressed (missed Dispose).</summary>
    private void Finalizers(List<Finding> findings, CancellationToken cancellation)
    {
        FinalizerReport report = new FinalizerAnalyzer(session).Analyze(cancellation);
        if (report.TotalObjects < 64)
        {
            return;
        }

        string top = string.Join(", ", report.ByType.Take(3).Select(s => $"{ShortType(s.TypeName)} x{s.Count:N0}"));
        findings.Add(new Finding(FindingSeverity.Warning, "finalizers",
            $"{report.TotalObjects:N0} finalizable objects awaiting finalization ({Bytes(report.TotalBytes)})",
            $"A live finalizer that wasn't suppressed usually means a missing Dispose(). Most common: {top}.")
        {
            Bytes = (long)report.TotalBytes,
            Count = report.TotalObjects,
            NextCommand = "finalizers",
        });
    }

    private void DuplicateStrings(List<Finding> findings, long heapBytes)
    {
        IReadOnlyList<DuplicateString> dups = new HeapAnalyzer(session).FindDuplicateStrings(limit: 100);
        long wasted = dups.Sum(d => (long)d.WastedBytes);
        if (wasted < 64 * 1024 && (heapBytes == 0 || (double)wasted / heapBytes < 0.02))
        {
            return;
        }

        DuplicateString? top = dups.FirstOrDefault();
        findings.Add(new Finding(FindingSeverity.Warning, "duplicate-strings",
            $"Duplicate strings waste {Bytes((ulong)wasted)} across {dups.Count} values",
            top is null ? "Intern or dedupe them." : $"e.g. \"{Preview(top.Value)}\" x{top.Count}. Intern or dedupe them.")
        {
            Bytes = wasted,
            NextCommand = "strings",
        });
    }

    private static void Fragmentation(List<Finding> findings, IReadOnlyList<HeapTypeStat> histogram, long heapBytes)
    {
        HeapTypeStat? free = histogram.FirstOrDefault(s => s.TypeName == "Free");
        if (free is null || heapBytes == 0)
        {
            return;
        }

        double pct = 100.0 * free.TotalSize / heapBytes;
        if (pct < 25)
        {
            return;
        }

        findings.Add(new Finding(FindingSeverity.Warning, "fragmentation",
            $"Heap fragmentation: {Bytes(free.TotalSize)} free ({pct:0}% of heap)",
            "A high free-space ratio means fragmentation (or a large collection that just happened).")
        {
            Bytes = (long)free.TotalSize,
            NextCommand = "segments",
        });
    }

    /// <summary>A user type with a very large instance count - a candidate for unbounded growth.</summary>
    private static void Growth(List<Finding> findings, IReadOnlyList<HeapTypeStat> histogram)
    {
        HeapTypeStat? suspect = histogram
            .Where(s => s.Count >= 10_000 && !IsFramework(s.TypeName) && s.TypeName != "Free")
            .OrderByDescending(s => s.TotalSize)
            .FirstOrDefault();
        if (suspect is null)
        {
            return;
        }

        findings.Add(new Finding(FindingSeverity.Info, "growth",
            $"{suspect.Count:N0} instances of {ShortType(suspect.TypeName)} ({Bytes(suspect.TotalSize)})",
            "A large population - check for unbounded growth (a cache or list that never shrinks).")
        {
            Type = suspect.TypeName,
            Bytes = (long)suspect.TotalSize,
            Count = suspect.Count,
            NextCommand = $"objects {ShortType(suspect.TypeName)}",
        });
    }

    private static bool IsCollection(string type) =>
        type.Contains("List<") || type.Contains("Dictionary<") || type.Contains("HashSet<") ||
        type.Contains("Queue<") || type.Contains("Stack<") || type.Contains("[]");

    private static bool IsFramework(string type) =>
        type.StartsWith("System.", StringComparison.Ordinal) || type.StartsWith("Microsoft.", StringComparison.Ordinal);

    private static string Bytes(ulong bytes) => ByteFormat.Human(bytes);
    private static string Bytes(long bytes) => ByteFormat.Human(bytes);

    /// <summary>Strips the namespace (and generic argument noise) for a compact type name.</summary>
    private static string ShortType(string type)
    {
        int generic = type.IndexOf('<');
        string head = generic >= 0 ? type[..generic] : type;
        int dot = head.LastIndexOf('.');
        return dot >= 0 ? type[(dot + 1)..] : type;
    }

    private static string Preview(string value)
    {
        value = value.Replace('\n', ' ').Replace('\r', ' ');
        return value.Length <= 48 ? value : value[..48] + "...";
    }
}
