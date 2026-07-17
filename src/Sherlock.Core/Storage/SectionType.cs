namespace Sherlock.Core.Storage;

public enum SectionType : uint
{
    Strings = 1,
    Frames = 2,
    Stacks = 3,
    StackFrames = 4,
    Allocations = 5,
    Correlation = 6,

    // Heap-graph sections (the persisted object graph — see Sherlock.Core.HeapModel). Columnar POD
    // arrays indexed by dense object id; the synthetic root is the last node, its successors are the
    // GC roots, so roots need no separate section.
    GraphAddresses = 7,   // ulong[] object addresses, sorted (id = index)
    GraphSizes = 8,       // uint[] shallow sizes, by id
    GraphOffsets = 9,     // int[] CSR row offsets (length ObjectCount + 2)
    GraphEdges = 10,      // int[] CSR successor ids
}
