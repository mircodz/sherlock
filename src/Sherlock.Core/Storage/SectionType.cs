namespace Sherlock.Core.Storage;

public enum SectionType : uint
{
    // Profile: methods by identity, allocated types, stacks of method indices, allocation counts by stack and type,
    // and the allocating stack of each object alive at capture. Keep in sync with the native SectionType.
    Strings = 1,
    Modules = 2,
    Methods = 3,
    Types = 4,
    Stacks = 5,
    StackFrames = 6,
    Allocations = 7,
    Correlation = 8,

    // Graph: dense object ids in address order; the node after the last object is the synthetic root, whose
    // successors are the rooted objects. GraphRoots keeps every root's CLR metadata.
    GraphMeta = 9,       // ulong[4]: free bytes, free count, source dump length, source dump mtime
    GraphAddresses = 10, // ulong per object, sorted
    GraphSizes = 11,     // uint per object
    GraphTypeIds = 12,   // int per object, indexing GraphTypeNames
    GraphTypeNames = 13, // [u32 count][(u32 length, utf8) x count]
    GraphOffsets = 14,   // long per object, then the root's, then the total: each one's first edge
    GraphEdges = 15,     // int per reference
    GraphRoots = 16,     // HeapRootRecord per CLR root

    // Dominators, in reverse postorder with the synthetic root first.
    DomMeta = 17,      // ulong[1]: the graph's ContentHash
    DomNodeByRpo = 18, // int: object id
    DomRetained = 19,  // ulong: retained bytes
    DomIdom = 20,      // int: immediate dominator
}
