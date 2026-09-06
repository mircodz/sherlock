namespace Sherlock.Core.Storage;

public enum SectionType : uint
{
    Strings = 1,
    Frames = 2,
    Stacks = 3,
    StackFrames = 4,
    Allocations = 5,
    Correlation = 6,

    // Heap columns use dense object ids; the last node is the synthetic root.
    // Its successors are rooted objects; GraphRoots preserves each root's CLR metadata.
    GraphAddresses = 7,   // ulong[] object addresses, sorted (id = index)
    GraphSizes = 8,       // uint[] shallow sizes, by id
    GraphOffsets = 9,     // long[] CSR row offsets (length ObjectCount + 2; long so edge count can exceed 2.1B)
    GraphEdges = 10,      // int[] CSR successor ids (legacy single-section edges; superseded by GraphEdgesChunk)
    GraphTypeIds = 11,    // int[] per-object type index into GraphTypeNames (optional)
    GraphTypeNames = 12,  // blob: [u32 count][ (u32 len, utf8 bytes) x count ] (optional)
    GraphMeta = 13,       // ulong[4]: free bytes/count + source dump length/mtime
    GraphEdgesChunk = 14, // int[] one node-aligned slice of the CSR successor ids; repeated, in order, so
                          //   the edge column can exceed the ~2.1B single-array ceiling
    GraphEdgeChunkMeta = 15, // long[] first global edge index of each chunk (length chunkCount + 1, last = total)
    GraphRoots = 16,

    // Dominator columns use RPO ids, with the synthetic root at 0.
    // Address and shallow size are reconstructed from the graph via NodeByRpo.
    DomMeta = 20,         // ulong[1]: { graph ContentHash }; validity key, reject if it != the graph's
    DomNodeByRpo = 21,    // int[]   RPO -> object id (graph.Root at index 0)
    DomRetained = 23,     // ulong[] RPO -> retained size
    DomIdom = 24,         // int[]   RPO -> immediate dominator (RPO)
}
