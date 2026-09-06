using System.Linq;
using System.Text;
using Sherlock.Core.Profiling;

namespace Sherlock.CLI.Export;

/// <summary>Allocation profiles as <c>root;...;leaf bytes</c> lines for flamegraph tools.</summary>
public static class FoldedStacks
{
    /// <param name="survived">Use first-GC-survived bytes instead of allocated bytes.</param>
    public static string Write(AllocationProfile profile, bool survived = false)
    {
        var sb = new StringBuilder();
        foreach (AllocationSite site in profile.Sites)
        {
            long value = survived ? site.SurvivedBytes : site.AllocBytes;
            if (value <= 0)
            {
                continue;
            }

            // Escape legacy names containing the frame separator.
            sb.Append(string.Join(';', site.Frames.Select(frame => frame.Replace(';', ':'))));
            sb.Append(' ');
            sb.Append(value);
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
