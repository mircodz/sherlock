using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Sherlock.Core.Profiling;
using Sherlock.Core.Storage;
using Sherlock.Core.Store;
using Sherlock.Core.Tests.Common;
using Xunit;

namespace Sherlock.Core.Tests.Store;

public sealed class SnapshotStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void AddSnapshotUpdatesWorkspaceMetadata()
    {
        var store = new SnapshotStore(Path.Combine(_tmp.Path, "store"));
        Session session = store.BeginSession(SessionKind.Run);
        string dump = WriteFile("heap.tmp", [1, 2, 3]);
        string provenance = WriteProvenance("profile.tmp");

        SnapshotEntry snapshot = store.AddSnapshot(
            session, dump, moveIntoStore: true, sourcePid: 42,
            provenanceSource: provenance, correlated: true);

        Assert.True(snapshot.Exists);
        Assert.True(snapshot.HasAllocations);
        Assert.True(snapshot.HasCorrelation);
        Assert.True(snapshot.ProvenanceSizeBytes > 0);
        Assert.Equal(snapshot.SizeBytes + snapshot.ProvenanceSizeBytes, snapshot.TotalSizeBytes);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(snapshot.Path));
        string metadata = File.ReadAllText(Path.Combine(session.Dir, "metadata.json"));
        Assert.Contains($"\"id\": \"{snapshot.Id}\"", metadata);
        Assert.False(File.Exists(Path.Combine(snapshot.Dir, "metadata.json")));
        Assert.False(File.Exists(Path.Combine(store.Root, "catalog.json")));
        Assert.False(File.Exists(dump));
        Assert.False(File.Exists(provenance));
        Assert.DoesNotContain(Directory.EnumerateFiles(store.Root, "*.tmp", SearchOption.AllDirectories), _ => true);
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode publicBits =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Assert.Equal(0, (int)(File.GetUnixFileMode(store.Root) & publicBits));
        }
    }

    [Fact]
    public void CorrelatedSnapshotWithoutProvenanceIsRejectedBeforeMovingDump()
    {
        var store = new SnapshotStore(Path.Combine(_tmp.Path, "store"));
        Session session = store.BeginSession(SessionKind.Run);
        string dump = WriteFile("heap.tmp", [1]);

        Assert.Throws<InvalidDataException>(() => store.AddSnapshot(
            session, dump, moveIntoStore: true, correlated: true));

        Assert.True(File.Exists(dump));
        Assert.Empty(session.Snapshots);
    }

    [Fact]
    public void InvalidProvenanceIsRejectedBeforeMovingDump()
    {
        var store = new SnapshotStore(Path.Combine(_tmp.Path, "store"));
        Session session = store.BeginSession(SessionKind.Run);
        string dump = WriteFile("heap.tmp", [1]);
        string provenance = WriteFile("broken.slab", [0, 1, 2]);

        Assert.Throws<InvalidDataException>(() => store.AddSnapshot(
            session, dump, moveIntoStore: true, provenanceSource: provenance));

        Assert.True(File.Exists(dump));
        Assert.True(File.Exists(provenance));
        Assert.Empty(session.Snapshots);
    }

    [Fact]
    public async Task ConcurrentSnapshotsReceiveUniqueIdsAndPersist()
    {
        var store = new SnapshotStore(Path.Combine(_tmp.Path, "store"));
        Session session = store.BeginSession(SessionKind.Run);
        string[] dumps = Enumerable.Range(0, 8)
            .Select(i => WriteFile($"heap-{i}.tmp", [(byte)i]))
            .ToArray();

        SnapshotEntry[] snapshots = await Task.WhenAll(dumps.Select((dump, i) => Task.Run(() =>
            store.AddSnapshot(session, dump, moveIntoStore: true, sourcePid: i + 1))));

        Assert.Equal(8, snapshots.Select(s => s.Id).Distinct().Count());
        var reopened = new SnapshotStore(store.Root);
        Assert.Equal(8, reopened.Sessions.Single().Snapshots.Count());
    }

    [Fact]
    public void PersistenceFailurePreservesAnUnindexedBundle()
    {
        var store = new SnapshotStore(Path.Combine(_tmp.Path, "store"));
        Session session = store.BeginSession(SessionKind.Run);
        string dump = WriteFile("heap.tmp", [1, 2, 3]);
        string provenance = WriteProvenance("profile.tmp");

        string metadata = Path.Combine(session.Dir, "metadata.json");
        File.Delete(metadata);
        Directory.CreateDirectory(metadata);

        IOException error = Assert.ThrowsAny<IOException>(() =>
            store.AddSnapshot(
                session, dump, moveIntoStore: true, sourcePid: 42,
                provenanceSource: provenance));

        string bundle = Path.Combine(session.Dir, "snapshots", "s1");
        Assert.Contains(bundle, error.Message);
        Assert.False(File.Exists(dump));
        Assert.True(File.Exists(provenance));
        Assert.Empty(session.Snapshots);
        Assert.True(File.Exists(Path.Combine(bundle, "heap.dmp")));
        Assert.True(File.Exists(Path.Combine(bundle, "provenance.slab")));
        Assert.False(File.Exists(Path.Combine(bundle, "metadata.json")));
    }

    [Fact]
    public void RemoveFailureRestoresTheSnapshot()
    {
        var store = new SnapshotStore(Path.Combine(_tmp.Path, "store"));
        Session session = store.BeginSession(SessionKind.Run);
        SnapshotEntry snapshot = store.AddSnapshot(session, WriteFile("heap.tmp", [1]), moveIntoStore: true);
        string metadata = Path.Combine(session.Dir, "metadata.json");
        File.Delete(metadata);
        Directory.CreateDirectory(metadata);

        Assert.ThrowsAny<IOException>(() => store.Remove(snapshot.Id));

        Assert.NotNull(store.FindSnapshot(snapshot.Id));
        Assert.True(snapshot.Exists);
    }

    [Fact]
    public void SetLabelUpdatesWorkspaceMetadata()
    {
        var store = new SnapshotStore(Path.Combine(_tmp.Path, "store"));
        Session session = store.BeginSession(SessionKind.Run);
        SnapshotEntry snapshot = store.AddSnapshot(
            session, WriteFile("heap.tmp", [1]), moveIntoStore: true);

        SnapshotEntry updated = store.SetLabel(snapshot.Id, "after query")!;
        string json = File.ReadAllText(Path.Combine(session.Dir, "metadata.json"));

        Assert.Contains("\"label\": \"after query\"", json);
        Assert.False(File.Exists(Path.Combine(updated.Dir, "metadata.json")));
    }

    [Fact]
    public void StartupScansWorkspaceMetadataAndSkipsOrphans()
    {
        string root = Path.Combine(_tmp.Path, "store");
        Directory.CreateDirectory(Path.Combine(root, "w5"));
        var store = new SnapshotStore(root);
        Session session = store.BeginSession(SessionKind.Run);
        SnapshotEntry snapshot = store.AddSnapshot(session, WriteFile("heap.tmp", [1]), moveIntoStore: true);

        var reopened = new SnapshotStore(root);

        Assert.Equal("w6", session.Id);
        Assert.Equal(snapshot.Id, reopened.Sessions.Single().Snapshots.Single().Id);
    }

    [Fact]
    public void InvalidWorkspaceMetadataFailsClearly()
    {
        string root = Path.Combine(_tmp.Path, "store");
        string workspace = Path.Combine(root, "w1");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "metadata.json"), "{broken");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new SnapshotStore(root));

        Assert.Contains(Path.Combine(workspace, "metadata.json"), error.Message);
    }

    [Fact]
    public void WorkspacesRemainNumericallyOrderedAfterRestart()
    {
        string root = Path.Combine(_tmp.Path, "store");
        var store = new SnapshotStore(root);
        for (int i = 0; i < 12; i++)
        {
            store.BeginSession(SessionKind.Run);
        }

        var reopened = new SnapshotStore(root);

        Assert.Equal(Enumerable.Range(1, 12).Select(i => $"w{i}"), reopened.Sessions.Select(session => session.Id));
    }

    private string WriteProvenance(string name)
    {
        var provenance = new ProvenanceWriter();
        uint stack = provenance.InternStack(["Program.Main", "App.Query"]);
        provenance.AddAllocation(stack, provenance.InternType("MyApp.Query"), 128, 2, 64, 1);
        var container = new ContainerWriter();
        provenance.WriteTo(container);
        string path = Path.Combine(_tmp.Path, name);
        container.Save(path);
        return path;
    }

    private string WriteFile(string name, byte[] contents)
    {
        string path = Path.Combine(_tmp.Path, name);
        File.WriteAllBytes(path, contents);
        return path;
    }
}
