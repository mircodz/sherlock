using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Sherlock.CLI.Rendering;
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

    public void Execute(ReplContext context, string[] args)
    {
        double timeout = DefaultTimeoutSeconds;
        if (args.Length > 0 && double.TryParse(args[0], out double t) && t > 0)
        {
            timeout = t;
        }

        bool anyLive = context.Workspace.Targets.Any(target => !target.HasExited);
        if (!anyLive)
        {
            Output.Warning(context.Console, $"No live target to wait on.");
            return;
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(timeout);
        context.Console.Status().Start("Waiting for a trigger to fire…", _ =>
        {
            while (DateTime.UtcNow < deadline)
            {
                IReadOnlyList<TriggeredCaptureResult> caught = context.Workspace.PollTriggeredSnapshots();
                if (caught.Count > 0)
                {
                    foreach (TriggeredCaptureResult capture in caught)
                    {
                        if (capture.Entry is { } entry)
                        {
                            string contents = entry.HasAllocations ? "heap + allocations" : "heap only";
                            Output.Success(context.Console, $"[bold]{capture.Probe}[/] fired · snapshot [bold]{entry.Id}[/] [#808791]({contents})[/]");
                        }
                        else
                        {
                            Output.Error(context.Console, $"[bold]{capture.Probe}[/] fired but capture failed: {capture.Error}");
                        }
                    }
                    return;
                }
                Thread.Sleep(150);
            }
            Output.Warning(context.Console, $"Timed out waiting for a trigger.");
        });
    }
}
