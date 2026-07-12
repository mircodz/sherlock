using System.Linq;
using System.Text;
using Sherlock.Core.Profiling;

namespace Sherlock.CLI.Export;

/// <summary>
/// The allocation profile as collapsed/folded stacks - one line per call stack, <c>root;...;leaf value</c>,
/// the format Speedscope and flamegraph.pl read directly. Value is bytes allocated, or bytes that
/// survived their first GC when <paramref name="survived"/> is set.
/// </summary>
public static class FoldedStacks
{
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

            // Frames run root -> leaf; ';' separates them, so it can't appear inside a frame.
            sb.Append(string.Join(';', site.Frames.Select(Clean)));
            sb.Append(' ');
            sb.Append(value);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Clean(string frame) => frame.Replace(';', ':');
}
