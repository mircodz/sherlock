namespace Sherlock.Core.Storage;

public enum SectionType : uint
{
    Strings = 1,
    Frames = 2,
    Stacks = 3,
    StackFrames = 4,
    Allocations = 5,
    Correlation = 6,
}
