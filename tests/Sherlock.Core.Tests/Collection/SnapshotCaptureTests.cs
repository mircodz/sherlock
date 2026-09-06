using System;
using System.IO;
using Sherlock.Core.Collection;
using Sherlock.Core.Profiling;
using Sherlock.Core.Storage;
using Sherlock.Core.Store;
using Sherlock.Core.Tests.Common;
using Xunit;

namespace Sherlock.Core.Tests.Collection;

public sealed class SnapshotCaptureTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Theory]
    [InlineData(0, 0, ProvenanceState.Exact)]
    [InlineData(10, 10, ProvenanceState.Exact)]
    [InlineData(10, 11, ProvenanceState.Drifted)]
    [InlineData(10, 9, ProvenanceState.Drifted)]
    [InlineData(-1, 10, ProvenanceState.Unverified)]
    [InlineData(10, -1, ProvenanceState.Unverified)]
    [InlineData(-1, -1, ProvenanceState.Unverified)]
    public void BestEffortCorrelationRequiresKnownMatchingGcCounts(
        long gcAtEmit, long gcAfterDump, ProvenanceState expected)
    {
        Assert.Equal(expected, SnapshotCapture.CorrelationState(gcAtEmit, gcAfterDump));
    }

    [Fact]
    public void CataloguingFailureRetainsTheBundleAndSourceProvenance()
    {
        var store = new SnapshotStore(Path.Combine(_tmp.Path, "store"));
        Session session = store.BeginSession(SessionKind.Run);
        string dump = _tmp.File(".dmp");
        File.WriteAllBytes(dump, [1, 2, 3]);
        string provenance = _tmp.File();
        var writer = new ProvenanceWriter();
        uint stack = writer.InternStack(["Program.Main"]);
        writer.AddAllocation(stack, writer.InternType("Example"), 64, 1, 64, 1);
        var container = new ContainerWriter();
        writer.WriteTo(container);
        container.Save(provenance);
        var capture = new SnapshotCaptureResult(dump, provenance, ProvenanceState.Exact);

        string metadata = Path.Combine(session.Dir, "metadata.json");
        File.Delete(metadata);
        Directory.CreateDirectory(metadata);
        IOException failure = Assert.ThrowsAny<IOException>(() =>
            store.AddSnapshot(session, capture.DumpPath, moveIntoStore: true, sourcePid: 42,
                provenanceSource: capture.ProvenancePath, correlated: true));
        DumpAnalysisException error = SnapshotCapture.Failure(42, failure, capture);

        string bundle = Path.Combine(session.Dir, "snapshots", "s1");
        Assert.Same(failure, error.InnerException);
        Assert.Contains(bundle, error.Message);
        Assert.Contains($"allocation data '{provenance}'", error.Message);
        Assert.DoesNotContain($"heap dump '{dump}'", error.Message);
        Assert.False(File.Exists(dump));
        Assert.True(File.Exists(provenance));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(bundle, "heap.dmp")));
        ProvenanceReader.ValidateFile(Path.Combine(bundle, "provenance.slab"));
        Assert.Empty(session.Snapshots);
    }
}
