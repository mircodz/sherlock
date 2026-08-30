using Sherlock.Core.Store;

namespace Sherlock.Core;

public sealed class SnapshotLibrary(SnapshotStore store)
{
    public SnapshotStore Store => store;

    public Snapshot Open(string idOrLabel)
    {
        if (store.FindSnapshot(idOrLabel) is not (_, { } entry))
        {
            throw new DumpAnalysisException($"no snapshot '{idOrLabel}'.");
        }
        if (!entry.Exists)
        {
            throw new DumpAnalysisException($"snapshot '{idOrLabel}' file is missing.");
        }
        return new Snapshot(DumpSession.Open(entry.Path), entry);
    }
}
