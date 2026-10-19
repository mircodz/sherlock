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
    // successors are the rooted objects. Type names are Strings and Types, as in a profile.
    GraphMeta = 16,       // GraphMeta: graph id, free space, and the source dump's length and mtime
    GraphAddresses = 17,  // ulong per object, sorted
    GraphSizes = 18,      // uint per object; uint.MaxValue marks one in GraphLargeSizes
    GraphLargeSizes = 19, // LargeSize per object of 4 GiB or more, by id
    GraphTypeIds = 20,    // int per object, indexing Types
    GraphOffsets = 21,    // long per object, then the root's, then the total: each one's first edge
    GraphEdges = 22,      // int per reference
    GraphRoots = 23,      // HeapRootRecord per CLR root

    // Dominators, by graph node: object ids, then the synthetic root.
    DomMeta = 32,         // Guid: the id of the graph the tree was computed from
    DomIdom = 33,         // int per node: immediate dominator; the root's is itself, an unreachable object's -1
    DomRetained = 34,     // ulong per node: retained bytes
    DomChildOffsets = 35, // long per node, then the total: each node's first entry in DomChildren
    DomChildren = 36,     // int per reachable object: its dominator's children, largest retained first
}
