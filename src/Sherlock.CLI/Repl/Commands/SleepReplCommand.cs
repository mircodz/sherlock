using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// Pauses for a number of seconds. Mainly for scripts that drive a live target and
/// need to give it time to reach a state worth snapshotting (e.g. after <c>run</c>,
/// before <c>correlate</c>).
/// </summary>
public sealed class SleepReplCommand : IReplCommand
{
    public string Name => "sleep";
    public IReadOnlyList<string> Aliases => ["wait"];
    public string Summary => "Pause for N seconds (useful in scripts before snapshotting a live target).";
    public string Category => "Live";
    public string Usage => "sleep <seconds>";

    public void Execute(ReplContext context, string[] args)
    {
        Args.Require(args, 1, Usage);
        if (!double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds < 0)
        {
            throw new DumpAnalysisException($"'{args[0]}' is not a valid duration in seconds.");
        }

        context.Console.MarkupLineInterpolated($"[grey]sleeping {seconds:0.##}s…[/]");
        Thread.Sleep((int)(seconds * 1000));
    }
}
