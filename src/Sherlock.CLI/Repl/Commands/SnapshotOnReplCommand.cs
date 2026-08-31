using System;
using System.Collections.Generic;
using System.Linq;
using Sherlock.CLI.Rendering;
using Sherlock.Core.Collection;
using Spectre.Console;

namespace Sherlock.CLI.Repl.Commands;

/// <summary>
/// Arms an event-driven snapshot trigger on a live target. Events:
///   call:Ns.Type.Method   a method is entered (ReJIT; non-inlined methods only)
///   alloc:Ns.Type         an instance of the type is allocated
///   gc[:gen2]             after a (generation-N) garbage collection
///   throw[:Ns.Exception]  an exception (of that type) is thrown
/// A bare Ns.Type.Method is shorthand for call:.
/// </summary>
public sealed class SnapshotOnReplCommand : IReplCommand
{
    public string Name => "snapshot-on";
    public IReadOnlyList<string> Aliases => ["snapon"];
    public string Summary => "Capture a snapshot when an event fires (call/alloc/gc/throw) on a live target.";
    public string Category => "Live";
    public string Usage => "snapshot-on <call:Type.Method | alloc:Type | gc[:gen2] | throw[:Exception]>";

    public void Execute(ReplContext context, string[] args)
    {
        if (args.Length == 0)
        {
            Output.Error(context.Console, $"Usage: [bold]{Usage}[/]");
            return;
        }

        string spec = args[0];

        // Arm on the most recent live run with a control channel.
        RunTarget? target = context.Workspace.Targets
            .LastOrDefault(t => !t.HasExited && t.Features.Contains("snapshot-triggers"));
        if (target is null)
        {
            Output.Warning(context.Console, $"No live target with trigger support. Start one with [bold]run --correlate -- <app>[/].");
            return;
        }

        int armPid = target.PrimaryPid; // the app (child under a launcher, if any)
        (bool ok, string detail) = context.Console.Status()
            .Start($"Arming snapshot-on {spec}…", _ => target.ArmTrigger(armPid, spec, TimeSpan.FromSeconds(10)));

        if (ok)
        {
            Output.Success(context.Console, $"Armed [bold]{spec}[/] on [#00D7FF]{target.Name}[/] · pid {target.Pid}");
        }
        else
        {
            Output.Error(context.Console, $"Could not arm trigger: {detail}");
        }
    }
}
