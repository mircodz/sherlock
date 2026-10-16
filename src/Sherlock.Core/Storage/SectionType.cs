namespace Sherlock.Core.Storage;

/// <summary>Section types, numbered in blocks by the kind of file they appear in.</summary>
public enum SectionType : uint
{
    // Profile: methods by identity, allocated types, stacks of method indices, allocation counts by stack and type,
    // and, for objects alive at capture, their addresses (sorted) and allocating stacks. Keep in sync with the
    // native SectionType.
    Strings = 1,
    Modules = 2,
    Methods = 3,
    Types = 4,
    Stacks = 5,
    StackFrames = 6,
    Allocations = 7,
    ObjectAddresses = 8,
    ObjectStacks = 9,

    // Graph: dense object ids in address order; the node after the last object is the synthetic root, whose
    // successors are the rooted objects. GraphRoots keeps every root's CLR metadata.
    GraphMeta = 16,      // ulong[4]: free bytes, free count, source dump length, source dump mtime
    GraphAddresses = 17, // ulong per object, sorted
    GraphSizes = 18,     // uint per object
    GraphTypeIds = 19,   // int per object, indexing GraphTypeNames
    GraphTypeNames = 20, // [u32 count][(u32 length, utf8) x count]
    GraphOffsets = 21,   // long per object, then the root's, then the total: each one's first edge
    GraphEdges = 22,     // int per reference
    GraphRoots = 23,     // HeapRootRecord per CLR root

    // Dominators, in reverse postorder with the synthetic root first.
    DomMeta = 32,      // ulong[1]: the graph's ContentHash
    DomNodeByRpo = 33, // int: object id
    DomRetained = 34,  // ulong: retained bytes
    DomIdom = 35,      // int: immediate dominator
}
