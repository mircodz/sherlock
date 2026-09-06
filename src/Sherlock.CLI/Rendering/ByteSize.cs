using Sherlock.Core;

namespace Sherlock.CLI.Rendering;

/// <summary>Formats byte counts as human-readable sizes (e.g. 1.5 MB).</summary>
public static class ByteSize
{
    public static string Format(long bytes) => bytes < 0 ? "n/a" : ByteFormat.Human(bytes);
}
