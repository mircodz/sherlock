using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Sherlock.CLI.Rendering;
using Sherlock.Core;
using Sherlock.Core.Store;
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

        bool anyLive = context.Workspace.Targets.Any(target => !target.HasExited);
        if (!anyLive)
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
                        if (capture.Entry is { } entry)
                        {
                            string contents = entry.HasAllocations ? "heap + allocations" : "heap only";
                            Output.Success(context.Console, $"[bold]{capture.Probe}[/] fired · snapshot [bold]{entry.Id}[/] [#808791]({contents})[/]");
                            if (capture.Error is not null)
                            {
                                Output.Warning(context.Console, $"{capture.Error}");
                                result |= ReplResult.Failure;
                            }
                        }
                        else
                        {
                            Output.Error(context.Console, $"[bold]{capture.Probe}[/] fired but capture failed: {capture.Error}");
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
