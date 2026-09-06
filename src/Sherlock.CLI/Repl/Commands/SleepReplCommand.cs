using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Pauses for N seconds. For scripts that must let a live target reach a state worth snapshotting.</summary>
public sealed class SleepReplCommand : IReplCommand
{
    public string Name => "sleep";
    public IReadOnlyList<string> Aliases => ["wait"];
    public string Summary => "Pause for N seconds (useful in scripts before snapshotting a live target).";
    public string Category => "Live";
    public string Usage => "sleep <seconds>";

    public ReplResult Execute(ReplContext context, string[] args)
    {
        Args.Require(args, 1, Usage);
        if (!double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) ||
            !double.IsFinite(seconds) || seconds < 0 || seconds > int.MaxValue / 1000.0)
        {
            throw new DumpAnalysisException($"'{args[0]}' is not a valid duration in seconds.");
        }

        context.Console.MarkupLineInterpolated($"[#808791]sleeping {seconds:0.##}s…[/]");
        Task.Delay((int)(seconds * 1000), context.Cancellation).GetAwaiter().GetResult();
        return ReplResult.Success;
    }
}
