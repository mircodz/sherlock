namespace Sherlock.Core.Storage;

public enum SectionType : uint
{
    Strings = 1,
    Frames = 2,
    Stacks = 3,
    StackFrames = 4,
    Allocations = 5,
    Correlation = 6,

    // Heap-graph sections (the persisted object graph, see Sherlock.Core.HeapModel). Columnar POD arrays
    // indexed by dense object id; the synthetic root is the last node and its successors are the GC
    // roots, so roots need no separate section.
    GraphAddresses = 7,   // ulong[] object addresses, sorted (id = index)
    GraphSizes = 8,       // uint[] shallow sizes, by id
    GraphOffsets = 9,     // long[] CSR row offsets (length ObjectCount + 2; long so edge count can exceed 2.1B)
    GraphEdges = 10,      // int[] CSR successor ids (legacy single-section edges; superseded by GraphEdgesChunk)
    GraphTypeIds = 11,    // int[] per-object type index into GraphTypeNames (optional)
    GraphTypeNames = 12,  // blob: [u32 count][ (u32 len, utf8 bytes) x count ] (optional)
    GraphMeta = 13,       // ulong[1]: { FreeBytes } (optional scalar metadata)
    GraphEdgesChunk = 14, // int[] one node-aligned slice of the CSR successor ids; repeated, in order, so
                          //   the edge column can exceed the ~2.1B single-array ceiling
    GraphEdgeChunkMeta = 15, // long[] first global edge index of each chunk (length chunkCount + 1, last = total)

    // Derived dominator-tree cache (computed from the graph, cached beside the dump so reopen skips the
    // recompute, see Sherlock.Core.Analysis.DominatorTreeStore). RPO-indexed; index 0 is the root.
    // Address and own-size are NOT stored (they're the graph's columns re-permuted): NodeByRpo maps each
    // RPO slot to its object id, and on load address/own are looked up from the graph.
    DomMeta = 20,         // ulong[1]: { graph ContentHash }; validity key, reject if it != the graph's
    DomNodeByRpo = 21,    // int[]   RPO -> object id (graph index; -1 = synthetic root)
    DomRetained = 23,     // ulong[] RPO -> retained size
    DomIdom = 24,         // int[]   RPO -> immediate dominator (RPO)
}
