using System.Globalization;
using Sherlock.CLI.Rendering;
using Sherlock.Core;

namespace Sherlock.CLI.Repl;

/// <summary>
/// Argument parsing for REPL commands. Failures throw <see cref="DumpAnalysisException"/>, which the
/// REPL renders as <c>error: ...</c>, so commands don't repeat validate-and-return boilerplate.
/// </summary>
public static class Args
{
    public static void Require(string[] args, int count, string usage)
    {
        if (args.Length < count)
        {
            throw new DumpAnalysisException($"usage: {usage}");
        }
    }

    /// <summary>Parses the argument at <paramref name="index"/> as a hex object address.</summary>
    public static ulong Address(string[] args, int index, string usage)
    {
        if (args.Length <= index)
        {
            throw new DumpAnalysisException($"usage: {usage}");
        }
        if (!Addresses.TryParse(args[index], out ulong address))
        {
            throw new DumpAnalysisException($"'{args[index]}' is not a valid object address.");
        }
        return address;
    }

    /// <summary>A whole number at <paramref name="index"/>, or <paramref name="fallback"/> when absent. Anything else is
    /// an error rather than a silent default.</summary>
    public static int Count(string[] args, int index, int fallback, string usage, bool allowZero = false)
    {
        if (args.Length <= index)
        {
            return fallback;
        }
        if (!int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out int n) || (n == 0 && !allowZero))
        {
            throw new DumpAnalysisException($"'{args[index]}' is not a {(allowZero ? "non-negative" : "positive")} whole number. usage: {usage}");
        }
        return n;
    }
}
