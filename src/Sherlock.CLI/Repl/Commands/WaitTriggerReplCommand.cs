using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>Blocks until an armed snapshot trigger fires (and its dump is captured), or a timeout elapses.</summary>
public sealed class WaitTriggerReplCommand : IReplCommand
{
    private const double DefaultTimeoutSeconds = 30;

    public string Name => "wait-trigger";
    public IReadOnlyList<string> Aliases => ["waitfor"];
    public string Summary => "Wait until an armed snapshot trigger fires (or times out).";
    public string Category => "Live";
    public string Usage => "wait-trigger [seconds]";

    public ReplResult Execute(ReplContext context, string[] args)
    {
        double timeout = DefaultTimeoutSeconds;
        if (args.Length > 0)
        {
            if (!double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out timeout) ||
                !double.IsFinite(timeout) || timeout <= 0 || timeout > int.MaxValue / 1000.0)
            {
                throw new DumpAnalysisException($"'{args[0]}' is not a valid timeout in seconds.");
            }
        }

        if (!context.Workspace.Targets.Any(target => !target.HasExited))
        {
            Output.Warning(context.Console, $"No live target to wait on.");
            return ReplResult.Failure;
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(timeout);
        return context.Console.Status().Start("Waiting for a trigger to fire…", _ =>
        {
            while (DateTime.UtcNow < deadline)
            {
                context.Cancellation.ThrowIfCancellationRequested();
                IReadOnlyList<TriggeredCaptureResult> caught = context.Workspace.PollTriggeredSnapshots();
                if (caught.Count > 0)
                {
                    var result = ReplResult.Success;
                    foreach (TriggeredCaptureResult capture in caught)
                    {
                        if (!Output.TriggeredCapture(context.Console, capture))
                        {
                            result |= ReplResult.Failure;
                        }
                    }
                    return result;
                }
                Task.Delay(150, context.Cancellation).GetAwaiter().GetResult();
            }
            Output.Warning(context.Console, $"Timed out waiting for a trigger.");
            return ReplResult.Failure;
        });
    }
}
