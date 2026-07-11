using Sherlock.Core.Store;

namespace Sherlock.Core;

/// <summary>
/// Read-only access to the snapshot catalog: resolves an id/label to an open <see cref="Snapshot"/>.
/// Shared by the CLI and the MCP server so both front-ends open snapshots the same way.
/// </summary>
public sealed class SnapshotLibrary(SnapshotStore store)
{
    public SnapshotStore Store => store;

    /// <summary>Opens a snapshot by id or label. The caller owns (and disposes) the result.</summary>
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
